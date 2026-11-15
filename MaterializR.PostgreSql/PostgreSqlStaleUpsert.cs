using Dapper;
using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
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

        var identityProperties = GetSelectedProperties(entityType, keySelector);
        var conflictProperties = new[] { tenantProperty }.Concat(identityProperties).Distinct().ToArray();
        EnsureUniqueConflictTarget(entityType, conflictProperties);

        var runtimeEntityTypes = rows
            .Select(row => dbContext.Model.FindRuntimeEntityType(row.GetType()) ?? entityType)
            .Distinct()
            .ToArray();
        if (runtimeEntityTypes.Any(rowType => rowType.GetTableName() != tableName || rowType.GetSchema() != schema))
        {
            throw new InvalidOperationException("All stale cache row types must map to the same table and schema.");
        }

        var mappedColumns = runtimeEntityTypes.SelectMany(rowType => rowType.GetProperties())
            .Distinct()
            .Select(property => (Property: property, Column: property.GetColumnName(table)))
            .Where(item => item.Column is not null)
            .Where(item =>
                item.Property == staleProperty ||
                item.Property == tenantProperty ||
                conflictProperties.Contains(item.Property) ||
                (item.Property.ValueGenerated == ValueGenerated.Never &&
                 item.Property.GetComputedColumnSql(table) is null))
            .ToArray();

        if (mappedColumns.All(item => item.Property != staleProperty))
        {
            throw new InvalidOperationException($"The {nameof(ICacheEntity.IsStale)} property is not mapped to table {tableName}.");
        }

        var sqlHelper = dbContext.GetService<ISqlGenerationHelper>();
        var quotedTable = sqlHelper.DelimitIdentifier(tableName, schema);
        var insertColumns = string.Join(", ", mappedColumns.Select(item => sqlHelper.DelimitIdentifier(item.Column!)));
        var conflictColumns = string.Join(", ", conflictProperties.Select(property =>
            sqlHelper.DelimitIdentifier(property.GetColumnName(table)
                ?? throw new InvalidOperationException($"Property {property.Name} is not mapped to table {tableName}."))));
        var staleColumn = sqlHelper.DelimitIdentifier(staleProperty.GetColumnName(table)
            ?? throw new InvalidOperationException($"Property {staleProperty.Name} is not mapped to table {tableName}."));
        var connection = dbContext.Database.GetDbConnection();

        var batchSize = Math.Max(1, MaxParametersPerCommand / mappedColumns.Length);
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
                var parameters = new DynamicParameters();
                var values = new List<string>(batch.Length);
                var parameterIndex = 0;

                foreach (var row in batch)
                {
                    var parameterNames = new List<string>(mappedColumns.Length);
                    foreach (var (property, _) in mappedColumns)
                    {
                        var parameterName = $"p{parameterIndex++}";
                        object? value = property == tenantProperty
                            ? tenantId
                            : property == staleProperty
                                ? true
                                : GetPropertyValue(dbContext, row, property, entityType);
                        var converter = property.GetRelationalTypeMapping().Converter;
                        parameters.Add(parameterName, converter is null ? value : converter.ConvertToProvider(value));
                        parameterNames.Add($"@{parameterName}");
                    }

                    values.Add($"({string.Join(", ", parameterNames)})");
                }

                var commandText = $"INSERT INTO {quotedTable} ({insertColumns}) VALUES {string.Join(", ", values)} " +
                    $"ON CONFLICT ({conflictColumns}) DO UPDATE SET {staleColumn} = TRUE";
                await connection.ExecuteAsync(new CommandDefinition(
                    commandText,
                    parameters,
                    dbTransaction,
                    cancellationToken: cancellationToken));
            }

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            SynchronizeTrackedRows(dbContext, tenantId, rows, conflictProperties, tenantProperty, staleProperty, entityType);
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

    private static IProperty[] GetSelectedProperties(IEntityType entityType, LambdaExpression selector)
    {
        var members = new List<PropertyInfo>();
        CollectProperties(selector.Body, members);
        if (members.Count == 0)
        {
            throw new ArgumentException("The key selector must select one or more mapped properties.", nameof(selector));
        }

        return members.Select(member =>
        {
            var property = entityType.FindProperty(member.Name);
            return property is not null && property.PropertyInfo == member
                ? property
                : throw new ArgumentException($"Property {member.Name} is not mapped on {entityType.ClrType.Name}.", nameof(selector));
        }).Distinct().ToArray();
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
                throw new ArgumentException("The key selector must select mapped properties.", nameof(expression));
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

            var value = dbContext.Entry(row).Property(property.Name).CurrentValue;
            if (value is null && !property.IsNullable && property.GetDefaultValue() is null)
            {
                throw new InvalidOperationException(
                    $"Required shadow property {property.Name} on {entityType.ClrType.Name} needs a value or a database default for stale-row upserts.");
            }

            return value ?? property.GetDefaultValue();
        }

        return property.GetGetter().GetClrValue(row);
    }

    private static void SynchronizeTrackedRows<TDbContext, TCacheEntity, TTenantKey>(
        TDbContext dbContext,
        TTenantKey tenantId,
        TCacheEntity[] rows,
        IProperty[] conflictProperties,
        IProperty tenantProperty,
        IProperty staleProperty,
        IEntityType entityType)
        where TDbContext : DbContext
        where TCacheEntity : class, ITenantCacheEntity<TTenantKey>
        where TTenantKey : notnull
    {
        var identities = rows.Select(row => conflictProperties
            .Select(property => property == tenantProperty ? tenantId : GetPropertyValue(dbContext, row, property, entityType))
            .ToArray()).ToArray();

        foreach (var entry in dbContext.ChangeTracker.Entries<TCacheEntity>().ToArray())
        {
            if (entry.State == EntityState.Detached) continue;

            var matches = identities.Any(identity => conflictProperties.Select((property, index) =>
                ValuesEqual(property, entry.Property(property.Name).CurrentValue, identity[index])).All(match => match));
            if (!matches) continue;
            if (entry.State is EntityState.Added or EntityState.Deleted)
            {
                entry.State = EntityState.Detached;
                continue;
            }

            var staleEntry = entry.Property(staleProperty.Name);
            staleEntry.CurrentValue = true;
            staleEntry.OriginalValue = true;
            staleEntry.IsModified = false;
        }
    }

    private static bool ValuesEqual(IProperty property, object? left, object? right)
    {
        var comparer = property.GetKeyValueComparer() ?? property.GetValueComparer();
        return comparer?.Equals(left, right) ?? Equals(left, right);
    }
}
