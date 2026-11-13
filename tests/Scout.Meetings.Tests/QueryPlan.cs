using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Scout.Meetings.Tests;

/// <summary>Runs SQLite's EXPLAIN QUERY PLAN over the SQL Entity Framework generates for a store's query.</summary>
internal static partial class QueryPlan
{
    [GeneratedRegex(@"^SCAN \w+$")]
    private static partial Regex BareScan();

    /// <summary>A step that reads every row of a table, not of an index.</summary>
    internal static bool IsTableScan(string step) => BareScan().IsMatch(step);

    /// <summary>The plan's steps, in order, for the query's own SQL with its parameters bound.</summary>
    internal static async Task<List<string>> OfAsync(DbContext db, IQueryable query)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        var sql = new StringBuilder();
        foreach (var line in query.ToQueryString().Split('\n'))
        {
            if (line.StartsWith(".param set ", StringComparison.Ordinal))
            {
                var parts = line[".param set ".Length..].Split(' ', 2);
                var parameter = command.CreateParameter();
                parameter.ParameterName = parts[0];
                parameter.Value = long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : parts[1].Trim('\'');
                command.Parameters.Add(parameter);
            }
            else
            {
                sql.AppendLine(line);
            }
        }

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            steps.Add(reader.GetString(3));
        }

        return steps;
    }
}
