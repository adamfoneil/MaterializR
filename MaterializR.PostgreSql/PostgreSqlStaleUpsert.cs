using Dapper;
using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Linq.Expressions;
using System.Reflection;

namespace MaterializR.PostgreSql;

internal static class PostgreSqlStaleUpsert
{
    private const int MaxParametersPerCommand = 60000;

    internal static async Task ExecuteAsync<TDbContext, TCacheEntity, TTenantKey>(
        TDbContext dbContext,
        TTenantKey tenantId,
        TCacheEntity[] rows,
        LambdaExpression keySelector,
        LambdaExpression factSelector,
        IReadOnlyList<JsonFact<TCacheEntity>> jsonFacts,
        bool refresh,
        CancellationToken cancellationToken)
        where TDbContext : DbContext
        where TCacheEntity : class, ITenantCacheEntity<TTenantKey>
        where TTenantKey : notnull
    {
        if (rows.Length == 0) return;

        var entityType = dbContext.Model.FindEntityType(typeof(TCacheEntity))
            ?? throw new InvalidOperationException($"Entity type {typeof(TCacheEntity).Name} is not in the EF model.");
        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException($"Entity type {typeof(TCacheEntity).Name} is not mapped to a table.");
        var schema = entityType.GetSchema();
        var table = StoreObjectIdentifier.Table(tableName, schema);
        var tenantProperty = entityType.FindProperty(nameof(ITenantCacheEntity<>.TenantId))
            ?? throw new InvalidOperationException($"The {nameof(ITenantCacheEntity<>.TenantId)} property is not mapped.");
        var staleProperty = entityType.FindProperty(nameof(ICacheEntity.IsStale))
            ?? throw new InvalidOperationException($"The {nameof(ICacheEntity.IsStale)} property is not mapped.");
        var identityProperties = GetSelectedProperties(entityType, keySelector, "key");
        var conflictProperties = new[] { tenantProperty }.Concat(identityProperties).Distinct().ToArray();
        ArgumentNullException.ThrowIfNull(jsonFacts);
        var factProperties = GetFactProperties(entityType, factSelector, conflictProperties, tenantProperty, staleProperty, table, jsonFacts.Count > 0);
        EnsureUniqueConflictTarget(entityType, conflictProperties);

        var runtimeEntityTypes = rows
            .Select(row => dbContext.Model.FindRuntimeEntityType(row.GetType()) ?? entityType)
            .Distinct()
            .ToArray();
        if (runtimeEntityTypes.Any(rowType => rowType.GetTableName() != tableName || rowType.GetSchema() != schema))
        {
            throw new InvalidOperationException("All cache row types in an upsert must map to the same table and schema.");
        }

        var allProperties = runtimeEntityTypes.SelectMany(rowType => rowType.GetProperties()).Distinct().ToArray();
        ValidateJsonFacts(dbContext, entityType, table, jsonFacts, allProperties, factProperties, conflictProperties, tenantProperty, staleProperty);
        var jsonColumnNames = jsonFacts.Select(fact => fact.ColumnName).ToHashSet(StringComparer.Ordinal);
        var mappedColumns = allProperties
            .Select(property => (Property: property, Column: property.GetColumnName(table)))
            .Where(item => item.Column is not null && !jsonColumnNames.Contains(item.Column))
            .Where(item =>
                item.Property == staleProperty ||
                item.Property == tenantProperty ||
                conflictProperties.Contains(item.Property) ||
                (item.Property.ValueGenerated == ValueGenerated.Never && item.Property.GetComputedColumnSql(table) is null))
            .ToArray();

        if (mappedColumns.All(item => item.Property != staleProperty))
        {
            throw new InvalidOperationException($"The {nameof(ICacheEntity.IsStale)} property is not mapped to table {tableName}.");
        }

        var sqlHelper = dbContext.GetService<ISqlGenerationHelper>();
        var quotedTable = sqlHelper.DelimitIdentifier(tableName, schema);
        var insertColumns = mappedColumns.Select(item => sqlHelper.DelimitIdentifier(item.Column!))
            .Concat(jsonFacts.Select(fact => sqlHelper.DelimitIdentifier(fact.ColumnName))).ToArray();
        var conflictColumns = conflictProperties.Select(property =>
            sqlHelper.DelimitIdentifier(GetColumnName(property, table, tableName))).ToArray();
        var staleColumn = sqlHelper.DelimitIdentifier(GetColumnName(staleProperty, table, tableName));
        var assignments = refresh
            ? string.Join(", ", factProperties.Select(property => GetColumnName(property, table, tableName))
                .Concat(jsonFacts.Select(fact => fact.ColumnName))
                .Select(column => $"{sqlHelper.DelimitIdentifier(column)} = EXCLUDED.{sqlHelper.DelimitIdentifier(column)}")
                .Append($"{staleColumn} = FALSE"))
            : $"{staleColumn} = TRUE";
        // PostgreSQL cannot update a conflict target twice in one INSERT; the last row wins.
        var rowKeyProperties = conflictProperties.Where(property => property != tenantProperty).ToArray();
        var uniqueRows = new Dictionary<object?[], TCacheEntity>(new ConflictKeyComparer(rowKeyProperties));
        foreach (var row in rows)
        {
            var key = rowKeyProperties.Select(property => GetPropertyValue(dbContext, row, property, entityType)).ToArray();
            uniqueRows[key] = row;
        }
        rows = [.. uniqueRows.Values];
        var connection = dbContext.Database.GetDbConnection();
        var batchSize = Math.Max(1, MaxParametersPerCommand / insertColumns.Length);
        IDbContextTransaction? transaction = null;
        try
        {
            if (rows.Length > batchSize && dbContext.Database.CurrentTransaction is null)
            {
                transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            }

            var dbTransaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var batch in rows.Chunk(batchSize))
            {
                var values = batch.Select(row => mappedColumns.Select(item =>
                {
                    var property = item.Property;
                    object? value = property == tenantProperty
                        ? tenantId
                        : property == staleProperty
                            ? !refresh
                            : GetPropertyValue(dbContext, row, property, entityType);
                    var converter = property.GetRelationalTypeMapping().Converter;
                    return converter is null ? value : converter.ConvertToProvider(value);
                }).Concat(jsonFacts.Select(fact => (object?)new JsonbParameter(fact.Serialize(row)))).ToArray());

                var (commandText, parameters) = UpsertSql.Statement(quotedTable, insertColumns, conflictColumns, assignments, values);
                await connection.ExecuteAsync(new CommandDefinition(
                    commandText,
                    parameters,
                    dbTransaction,
                    cancellationToken: cancellationToken));
            }

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    internal static async Task<TCacheEntity[]> LockStaleRowsAsync<TDbContext, TCacheEntity, TTenantKey>(
        TDbContext dbContext,
        TTenantKey tenantId,
        LambdaExpression keySelector,
        CancellationToken cancellationToken)
        where TDbContext : DbContext
        where TCacheEntity : class, ITenantCacheEntity<TTenantKey>
        where TTenantKey : notnull
    {
        var entityType = dbContext.Model.FindEntityType(typeof(TCacheEntity))
            ?? throw new InvalidOperationException($"Entity type {typeof(TCacheEntity).Name} is not in the EF model.");
        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException($"Entity type {typeof(TCacheEntity).Name} is not mapped to a table.");
        var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        var tenantProperty = entityType.FindProperty(nameof(ITenantCacheEntity<>.TenantId))
            ?? throw new InvalidOperationException($"The {nameof(ITenantCacheEntity<>.TenantId)} property is not mapped.");
        var staleProperty = entityType.FindProperty(nameof(ICacheEntity.IsStale))
            ?? throw new InvalidOperationException($"The {nameof(ICacheEntity.IsStale)} property is not mapped.");
        var keyProperties = GetSelectedProperties(entityType, keySelector, "key");
        var sqlHelper = dbContext.GetService<ISqlGenerationHelper>();
        var tenantColumn = sqlHelper.DelimitIdentifier(GetColumnName(tenantProperty, table, tableName));
        var staleColumn = sqlHelper.DelimitIdentifier(GetColumnName(staleProperty, table, tableName));
        var orderColumns = new[] { tenantProperty }.Concat(keyProperties).Distinct()
            .Select(property => sqlHelper.DelimitIdentifier(GetColumnName(property, table, tableName)));
        var quotedTable = sqlHelper.DelimitIdentifier(tableName, entityType.GetSchema());
        var commandText = $"SELECT * FROM {quotedTable} WHERE {tenantColumn} = @tenant AND {staleColumn} = TRUE " +
            $"ORDER BY {string.Join(", ", orderColumns)} FOR UPDATE";
        var converter = tenantProperty.GetRelationalTypeMapping().Converter;
        var providerTenantId = converter is null ? tenantId : converter.ConvertToProvider(tenantId);
        var parameter = new NpgsqlParameter("tenant", providerTenantId ?? DBNull.Value);
        return await dbContext.Set<TCacheEntity>()
            .FromSqlRaw(commandText, parameter)
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
    }

    private static string GetColumnName(IProperty property, StoreObjectIdentifier table, string tableName) =>
        property.GetColumnName(table)
        ?? throw new InvalidOperationException($"Property {property.Name} is not mapped to table {tableName}.");

    private static void ValidateJsonFacts<TCacheEntity>(
        DbContext dbContext, IEntityType entityType, StoreObjectIdentifier table,
        IReadOnlyList<JsonFact<TCacheEntity>> jsonFacts, IProperty[] properties, IProperty[] factProperties,
        IProperty[] conflictProperties, IProperty tenantProperty, IProperty staleProperty)
    {
        if (jsonFacts.Count == 0) return;

        var mappedTable = dbContext.Model.GetRelationalModel().FindTable(table.Name, table.Schema)
            ?? throw new InvalidOperationException($"Table {table.Name} is not in the EF relational model.");
        var discriminatorName = entityType.GetRootType().GetDiscriminatorPropertyName();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in jsonFacts)
        {
            if (fact is null || string.IsNullOrWhiteSpace(fact.ColumnName) || fact.Serialize is null)
            {
                throw new ArgumentException("Each JSON fact must have a column name and serializer.", nameof(jsonFacts));
            }
            if (!names.Add(fact.ColumnName))
            {
                throw new ArgumentException($"JSON fact column {fact.ColumnName} is specified more than once.", nameof(jsonFacts));
            }

            var column = mappedTable.FindColumn(fact.ColumnName);
            if (column is null || !string.Equals(column.StoreType, "jsonb", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"JSON fact column {fact.ColumnName} must be a mapped jsonb column on table {table.Name}.", nameof(jsonFacts));
            }

            var columnProperties = properties.Where(property => property.GetColumnName(table) == fact.ColumnName);
            if (columnProperties.Any(property => property == tenantProperty || property == staleProperty ||
                conflictProperties.Contains(property) || factProperties.Contains(property) ||
                property.Name == discriminatorName || property.ValueGenerated != ValueGenerated.Never ||
                property.GetComputedColumnSql(table) is not null))
            {
                throw new ArgumentException($"JSON fact column {fact.ColumnName} conflicts with a scalar fact or a protected or generated column.", nameof(jsonFacts));
            }
        }
    }

    private static IProperty[] GetSelectedProperties(IEntityType entityType, LambdaExpression selector, string purpose, bool allowEmpty = false)
    {
        var members = new List<PropertyInfo>();
        CollectProperties(selector.Body, members);
        if (members.Count == 0 && !allowEmpty)
        {
            throw new ArgumentException($"The {purpose} selector must select one or more mapped properties.", nameof(selector));
        }

        return [.. members.Select(member =>
        {
            var property = entityType.FindProperty(member.Name);
            return property is not null && property.PropertyInfo == member
                ? property
                : throw new ArgumentException($"Property {member.Name} is not mapped on {entityType.ClrType.Name}.", nameof(selector));
        }).Distinct()];
    }

    private static IProperty[] GetFactProperties(
        IEntityType entityType,
        LambdaExpression selector,
        IProperty[] conflictProperties,
        IProperty tenantProperty,
        IProperty staleProperty,
        StoreObjectIdentifier table,
        bool allowEmpty)
    {
        var properties = GetSelectedProperties(entityType, selector, "fact", allowEmpty);
        var rootType = entityType.GetRootType();
        var discriminatorName = rootType.GetDiscriminatorPropertyName();
        var discriminatorProperty = discriminatorName is null ? null : rootType.FindProperty(discriminatorName);
        foreach (var property in properties)
        {
            if (property == tenantProperty || property == staleProperty || conflictProperties.Contains(property) ||
                property == discriminatorProperty || property.ValueGenerated != ValueGenerated.Never ||
                property.GetComputedColumnSql(table) is not null)
            {
                throw new ArgumentException(
                    $"Fact property {property.Name} must not be a tenant, key, stale, generated, computed, or discriminator property.",
                    nameof(selector));
            }

            if (property.GetColumnName(table) is null)
            {
                throw new ArgumentException($"Fact property {property.Name} is not mapped to the cache table.", nameof(selector));
            }
        }

        return properties;
    }

    private static void CollectProperties(Expression expression, ICollection<PropertyInfo> properties)
    {
        while (expression is UnaryExpression unary && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = unary.Operand;
        }

        switch (expression)
        {
            case MemberExpression { Member: PropertyInfo property }:
                properties.Add(property);
                break;
            case NewExpression creation:
                foreach (var argument in creation.Arguments) CollectProperties(argument, properties);
                break;
            case MemberInitExpression initialization:
                foreach (var binding in initialization.Bindings.OfType<MemberAssignment>())
                {
                    CollectProperties(binding.Expression, properties);
                }
                break;
            default:
                throw new ArgumentException("The selector must select mapped properties.", nameof(expression));
        }
    }

    private static void EnsureUniqueConflictTarget(IEntityType entityType, IProperty[] conflictProperties)
    {
        var properties = conflictProperties.ToHashSet();
        var hasPrimaryOrAlternateKey = entityType.GetKeys().Any(key =>
            key.Properties.ToHashSet().SetEquals(properties) &&
            key.Properties.All(property => !property.IsNullable));
        var hasUniqueIndex = entityType.GetIndexes().Any(index =>
            index.IsUnique &&
            index.GetFilter() is null &&
            index.Properties.ToHashSet().SetEquals(properties) &&
            index.Properties.All(property => !property.IsNullable));

        if (!hasPrimaryOrAlternateKey && !hasUniqueIndex)
        {
            throw new InvalidOperationException(
                $"The tenant and cache key properties ({string.Join(", ", conflictProperties.Select(property => property.Name))}) " +
                $"must be covered by a primary key, alternate key, or unfiltered unique index with non-nullable columns on {entityType.ClrType.Name}.");
        }
    }

    private static object? GetPropertyValue<TDbContext, TCacheEntity>(
        TDbContext dbContext,
        TCacheEntity row,
        IProperty property,
        IEntityType entityType)
        where TDbContext : DbContext
        where TCacheEntity : class
    {
        var runtimeType = dbContext.Model.FindRuntimeEntityType(row.GetType()) ?? entityType;
        if (runtimeType.FindProperty(property.Name) is null)
        {
            return property.GetDefaultValue();
        }

        if (property.IsShadowProperty())
        {
            var rootType = runtimeType.GetRootType();
            var discriminatorName = rootType.GetDiscriminatorPropertyName();
            var discriminatorProperty = discriminatorName is null ? null : rootType.FindProperty(discriminatorName);
            if (property == discriminatorProperty)
            {
                return runtimeType.GetDiscriminatorValue();
            }

            var defaultValue = property.FindAnnotation(RelationalAnnotationNames.DefaultValue);
            if (defaultValue is null)
            {
                throw new InvalidOperationException(
                    $"Shadow property {property.Name} on {entityType.ClrType.Name} needs a configured default for an upsert.");
            }

            return defaultValue.Value;
        }

        return property.GetGetter().GetClrValue(row);
    }

    private sealed class ConflictKeyComparer(IProperty[] properties) : IEqualityComparer<object?[]>
    {
        public bool Equals(object?[]? left, object?[]? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            for (var i = 0; i < properties.Length; i++)
            {
                var comparer = properties[i].GetKeyValueComparer() ?? properties[i].GetValueComparer();
                if (!(comparer?.Equals(left[i], right[i]) ?? object.Equals(left[i], right[i]))) return false;
            }
            return true;
        }

        public int GetHashCode(object?[] key)
        {
            var hash = new HashCode();
            for (var i = 0; i < properties.Length; i++)
            {
                var comparer = properties[i].GetKeyValueComparer() ?? properties[i].GetValueComparer();
                hash.Add(key[i] is null ? 0 : comparer?.GetHashCode(key[i]) ?? key[i]!.GetHashCode());
            }
            return hash.ToHashCode();
        }
    }
}
