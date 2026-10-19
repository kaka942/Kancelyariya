using System;
using System.Globalization;

namespace Kancelyariya.Models;

public class Operation
{
    public int Id { get; set; }
    public int? ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Unit { get; set; } = "шт.";
    public string Type { get; set; } = "in"; // "in", "out", "writeoff"
    public int Qty { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty; // "yyyy-MM-dd HH:mm:ss"

    public string FormattedDate
    {
        get
        {
            if (DateTime.TryParseExact(Date, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt.ToString("dd.MM.yyyy HH:mm");
            }
            return Date;
        }
    }

    public string TypeName => Type switch
    {
        "in" => "Приход",
        "out" => "Выдача",
        "writeoff" => "Списание",
        _ => Type
    };

    public string DisplayQty => Type == "in" ? $"+{Qty} {Unit}" : $"−{Qty} {Unit}";

    public string DisplayRecipient => string.IsNullOrWhiteSpace(Recipient) ? "—" : Recipient;

    public string DisplayNote => string.IsNullOrWhiteSpace(Note) ? "—" : Note;

    public string ColorHex => Type switch
    {
        "in" => "#2563EB",
        "out" => "#DC2626",
        "writeoff" => "#DC2626",
        _ => "#64748B"
    };
}
