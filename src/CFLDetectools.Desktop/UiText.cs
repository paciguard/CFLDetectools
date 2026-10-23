using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace CFLDetectools.Desktop;

public sealed class UiText : INotifyPropertyChanged
{
    public static UiText Shared { get; } = new();
    private readonly Dictionary<string, string> _chinese;
    public string Language { get; private set; } = "zh-CN";
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private UiText()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("CFLDetectools.Desktop.Localization.chinese.json")
            ?? throw new InvalidOperationException("Missing Chinese UI catalog.");
        _chinese = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Invalid Chinese UI catalog.");
    }

    public void SetLanguage(string language)
    {
        if (language is not ("zh-CN" or "en")) throw new ArgumentException("Unsupported UI language.");
        if (Language == language) return;
        Language = language;
        Revision++;
        PropertyChanged?.Invoke(this, new(nameof(Revision)));
    }

    public string Translate(string english) => Language == "zh-CN" && _chinese.TryGetValue(english, out var chinese)
        ? chinese : english;

    public string Format(string english, params object[] args) => string.Format(CultureInfo.InvariantCulture,
        Translate(english), args.Select(arg => arg is string text ? Translate(text) : arg).ToArray());
}

public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(UiText.Revision))
    {
        Source = UiText.Shared,
        Mode = BindingMode.OneWay,
        Converter = new TextConverter(Key)
    }.ProvideValue(serviceProvider);

    private sealed class TextConverter(string key) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => UiText.Shared.Translate(key);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
