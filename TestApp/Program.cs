using System;
using System.Globalization;

class Program {
    static void Main() {
        string[] inputs = { "20,624.65", "20624.65", "RM 20,624.65", "20.624,65", "20,624.6500", "20 624.65" };
        foreach(var input in inputs) {
            string cleaned = System.Text.RegularExpressions.Regex.Replace(input.Trim(), @"[A-Za-z$£€¥\s]", "");
            bool success = decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d);
            Console.WriteLine($"Input: '{input}' -> Cleaned: '{cleaned}' -> Parsed: {d} (Success: {success})");
        }
    }
}
