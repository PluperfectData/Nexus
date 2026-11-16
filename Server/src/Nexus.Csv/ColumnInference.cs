namespace Nexus.Csv;

internal sealed class ColumnInference
{
    // DuckDB's DECIMAL holds at most 38 digits.
    private const int MaxPrecision = 38;

    private bool _boolean = true;
    private bool _int64 = true;
    private bool _decimal = true;
    private bool _float64 = true;
    private bool _date = true;
    private bool _timestamp = true;
    private bool _dateTime = true;
    private bool _seen;
    private bool _nullable;
    private int _integerDigits;
    private int _scale;

    public void Add(string? text)
    {
        if (text is null)
        {
            _nullable = true;
            return;
        }

        _seen = true;
        _boolean = _boolean && CsvValues.IsBoolean(text);
        _int64 = _int64 && CsvValues.TryInt64(text, out _);
        _float64 = _float64 && CsvValues.TryFloat64(text, out _);
        _date = _date && CsvValues.TryDate(text, out _);
        _timestamp = _timestamp && CsvValues.TryTimestamp(text, out _);
        _dateTime = _dateTime && CsvValues.TryDateTime(text, out _);

        if (_decimal && CsvValues.TryDecimal(text, out _, out var integerDigits, out var scale))
        {
            _integerDigits = Math.Max(_integerDigits, integerDigits);
            _scale = Math.Max(_scale, scale);
        }
        else
        {
            _decimal = false;
        }
    }

    public ColumnSpec Spec(string name)
    {
        var precision = Math.Max(1, _integerDigits + _scale);
        return true switch
        {
            _ when !_seen => new ColumnSpec(name, ColumnType.String, Nullable: true),
            _ when _boolean => new ColumnSpec(name, ColumnType.Boolean, _nullable),
            _ when _int64 => new ColumnSpec(name, ColumnType.Int64, _nullable),
            _ when _decimal && precision <= MaxPrecision => new ColumnSpec(name, ColumnType.Decimal, _nullable, precision, _scale),
            _ when _float64 => new ColumnSpec(name, ColumnType.Float64, _nullable),
            _ when _date => new ColumnSpec(name, ColumnType.Date, _nullable),
            _ when _timestamp => new ColumnSpec(name, ColumnType.Timestamp, _nullable),
            _ when _dateTime => new ColumnSpec(name, ColumnType.DateTime, _nullable),
            _ => new ColumnSpec(name, ColumnType.String, _nullable),
        };
    }
}
