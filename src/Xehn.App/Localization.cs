using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Xehn.App;

internal sealed class Localization : INotifyPropertyChanged
{
    public static readonly Localization Current = new();
    private string language = Resolve(null, CultureInfo.CurrentUICulture);
    private static readonly Lazy<Dictionary<string, (string English, string Persian)>> entries = new(Load);
    private static readonly Lazy<Regex> phrases = new(() => new Regex(
        string.Join("|", entries.Value.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape)), RegexOptions.Compiled));

    public string Language => language;
    public FlowDirection Direction => language == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public static string Resolve(string? saved, CultureInfo system)
        => saved is "zh" or "en" or "fa" ? saved : system.TwoLetterISOLanguageName switch
        { "zh" => "zh", "fa" => "fa", _ => "en" };

    public void Select(string? saved)
    {
        var next = Resolve(saved, CultureInfo.CurrentUICulture);
        if (language == next) return;
        language = next;
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        PropertyChanged?.Invoke(this, new(nameof(Direction)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string Text(string value) => Current.Translate(value);
    private string Translate(string value)
    {
        if (language == "zh" || string.IsNullOrEmpty(value) || !value.Any(c => c is >= '\u3400' and <= '\u9fff')) return value;
        string Lookup((string English, string Persian) entry) => language == "fa" ? entry.Persian : entry.English;
        return entries.Value.TryGetValue(value, out var exact)
            ? Lookup(exact)
            : phrases.Value.Replace(value, match => Lookup(entries.Value[match.Value]));
    }

    private static Dictionary<string, (string, string)> Load()
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("Xehn.App.Translations.tsv")!;
        using var reader = new StreamReader(stream);
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var fields = line.Split('\t');
            if (fields.Length != 3) throw new InvalidDataException("Invalid translation catalog entry: " + line);
            result.Add(fields[0], (fields[1].Replace("\\s", " "), fields[2].Replace("\\s", " ")));
        }
        return result;
    }
}

// Bind text to both the source and the language. Internal protocol/status
// values remain stable while the visible WPF text switches instantly.
public sealed class LocExtension(string text) : MarkupExtension
{
    public string Text { get; set; } = text;
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        LocalizeExtension.Create(new Binding(nameof(Text)) { Source = this }).ProvideValue(serviceProvider);
}

public sealed class LocalizeExtension(string path) : MarkupExtension
{
    internal static MultiBinding Create(Binding source)
    {
        var binding = new MultiBinding { Converter = TextConverter.Instance, Mode = BindingMode.OneWay };
        binding.Bindings.Add(source);
        binding.Bindings.Add(new Binding(nameof(Localization.Language)) { Source = Localization.Current });
        return binding;
    }
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Create(new Binding(path)).ProvideValue(serviceProvider);

    private sealed class TextConverter : IMultiValueConverter
    {
        public static readonly TextConverter Instance = new();
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => values[0] is string text ? Localization.Text(text) : "";
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
