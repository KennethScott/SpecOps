using System.Collections;
using System.Globalization;
using System.Management.Automation;

namespace SpecOps.Core.Execution;

/// <summary>One object a script emitted, flattened to text values for display and export.</summary>
public sealed record ResultRow(string TypeName, IReadOnlyList<string> Columns, IReadOnlyList<string?> Values)
{
    public string? this[string column]
    {
        get
        {
            var index = Columns.ToList().IndexOf(column);
            return index < 0 ? null : Values[index];
        }
    }
}

/// <summary>Rows of the same type, with the union of their columns (in first-seen order).</summary>
public sealed record ResultSet(string TypeName, IReadOnlyList<string> Columns, IReadOnlyList<ResultRow> Rows)
{
    /// <summary>Short type name for display, e.g. "Process" for System.Diagnostics.Process.</summary>
    public string DisplayName =>
        TypeName.Split('.', '+')[^1].Replace("PSCustomObject", "Object", StringComparison.Ordinal);

    public static IReadOnlyList<ResultSet> Group(IEnumerable<ResultRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return
        [
            .. rows.GroupBy(r => r.TypeName, StringComparer.Ordinal)
                .Select(g => new ResultSet(
                    g.Key,
                    [.. g.SelectMany(r => r.Columns).Distinct(StringComparer.Ordinal)],
                    [.. g]
                )),
        ];
    }
}

/// <summary>
/// Called from the PowerShell pipeline (as $__SpecOpsResults.Add($_)) for every object a script emits, so the objects
/// survive alongside the console text. Plain strings and numbers are text-only; formatting directives are skipped.
/// </summary>
public sealed class ResultCapture(ScriptRun run, int maxRows)
{
    private const int MaxColumns = 40;
    private const int MaxValueLength = 2000;
    private const int MaxListItems = 20;

    public void Add(object? item)
    {
        if (item is null || run.ResultCount >= maxRows)
        {
            if (item is not null)
            {
                run.MarkResultsTruncated();
            }

            return;
        }

        var pso = item as PSObject ?? PSObject.AsPSObject(item);
        if (IsTextOnly(pso.BaseObject))
        {
            return;
        }

        var columns = new List<string>();
        var values = new List<string?>();
        foreach (var column in ColumnsFor(pso).Take(MaxColumns))
        {
            columns.Add(column.Label);
            values.Add(Format(column.Read(pso)));
        }

        if (columns.Count > 0)
        {
            run.AddResult(
                new ResultRow(
                    pso.TypeNames.FirstOrDefault() ?? pso.BaseObject.GetType().FullName ?? "Object",
                    columns,
                    values
                )
            );
        }
    }

    private static bool IsTextOnly(object value) =>
        value is string or ValueType or DateTimeOffset
        || value
            .GetType()
            .Namespace?.StartsWith("Microsoft.PowerShell.Commands.Internal.Format", StringComparison.Ordinal) == true;

    private sealed record Column(string Label, Func<PSObject, object?> Read);

    // Table-view columns per type name, looked up once per run (null: the type has no table view).
    private readonly Dictionary<string, IReadOnlyList<Column>?> _tableViews = new(StringComparer.Ordinal);

    /// <summary>
    /// Same choice Format-Table makes: the type's table view (format data, e.g. files and processes), else its
    /// default display set, else all properties (e.g. Select-Object / [pscustomobject] output).
    /// </summary>
    private IEnumerable<Column> ColumnsFor(PSObject pso)
    {
        if (TableViewColumns(pso) is { } view)
        {
            return view;
        }

        if (
            pso.Members["PSStandardMembers"]?.Value is PSMemberSet standard
            && standard.Members["DefaultDisplayPropertySet"] is PSPropertySet displaySet
        )
        {
            return displaySet.ReferencedPropertyNames.Select(PropertyColumn);
        }

        return pso.Properties.Where(p => p.IsGettable).Select(p => PropertyColumn(p.Name));
    }

    private static Column PropertyColumn(string name) => new(name, pso => ReadProperty(pso, name));

    private IReadOnlyList<Column>? TableViewColumns(PSObject pso)
    {
        var typeName = pso.TypeNames.FirstOrDefault();
        if (typeName is null || typeName.EndsWith("PSCustomObject", StringComparison.Ordinal))
        {
            return null;
        }

        if (_tableViews.TryGetValue(typeName, out var cached))
        {
            return cached;
        }

        IReadOnlyList<Column>? columns = null;
        try
        {
            // Called from the script's pipeline thread, so a nested pipeline on the same runspace is allowed.
            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            var definitions = ps.AddCommand("Get-FormatData")
                .AddParameter("TypeName", pso.TypeNames.ToArray())
                .Invoke<ExtendedTypeDefinition>();
            var table = definitions
                .SelectMany(d => d.FormatViewDefinition)
                .Select(v => v.Control)
                .OfType<TableControl>()
                .FirstOrDefault(t => t.Rows.Count > 0);
            if (table is not null)
            {
                columns =
                [
                    .. table
                        .Rows[0]
                        .Columns.Select((c, i) => ViewColumn(c.DisplayEntry, table.Headers.ElementAtOrDefault(i))),
                ];
            }
        }
        catch (Exception ex) when (ex is RuntimeException or InvalidOperationException)
        {
            // No usable view: fall back to properties.
        }

        _tableViews[typeName] = columns is { Count: > 0 } ? columns : null;
        return _tableViews[typeName];
    }

    private static Column ViewColumn(DisplayEntry entry, TableControlColumnHeader? header)
    {
        if (entry.ValueType == DisplayEntryValueType.Property)
        {
            return new Column(string.IsNullOrEmpty(header?.Label) ? entry.Value : header.Label, pso =>
                ReadProperty(pso, entry.Value)
            );
        }

        // A computed column (e.g. "CPU(s)"): evaluate the view's script block with $_ set to the object.
        var script = ScriptBlock.Create(entry.Value);
        return new Column(string.IsNullOrEmpty(header?.Label) ? entry.Value : header.Label, pso =>
        {
            try
            {
                return script.InvokeWithContext(null, [new PSVariable("_", pso)]).FirstOrDefault();
            }
            catch (RuntimeException ex)
            {
                return $"(error: {ex.Message})";
            }
        });
    }

    private static object? ReadProperty(PSObject pso, string name)
    {
        try
        {
            return pso.Properties[name]?.Value;
        }
        catch (Exception ex) when (ex is GetValueException or RuntimeException or InvalidOperationException)
        {
            return $"(error: {ex.Message})";
        }
    }

    private static string? Format(object? value)
    {
        var text = value switch
        {
            null => null,
            PSObject pso => Format(pso.BaseObject),
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable list => string.Join(", ", list.Cast<object?>().Take(MaxListItems).Select(Format)),
            _ => value.ToString(),
        };

        return text is { Length: > MaxValueLength } ? text[..MaxValueLength] + "…" : text;
    }
}
