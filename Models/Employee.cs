namespace Kancelyariya.Models;

public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Dept { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Dept) ? Name : $"{Name} — {Dept}";
    public string RecipientName => string.IsNullOrWhiteSpace(Dept) ? Name : $"{Name} ({Dept})";
}
