# Bundled Fonts

The dashboard uses three OFL-licensed fonts. Download the regular weight `.ttf` files and drop them in this directory:

| Font | Source | Filename expected |
|---|---|---|
| Instrument Serif | https://fonts.google.com/specimen/Instrument+Serif | `InstrumentSerif-Regular.ttf` |
| Instrument Sans | https://fonts.google.com/specimen/Instrument+Sans | `InstrumentSans-Regular.ttf` |
| JetBrains Mono | https://fonts.google.com/specimen/JetBrains+Mono | `JetBrainsMono-Regular.ttf` |

After dropping the `.ttf` files in this folder, add them to the csproj as `Resource` items so WPF can load them via `pack://application:,,,/UI/Fonts/`:

```xml
<ItemGroup>
  <Resource Include="UI\Fonts\InstrumentSerif-Regular.ttf" />
  <Resource Include="UI\Fonts\InstrumentSans-Regular.ttf" />
  <Resource Include="UI\Fonts\JetBrainsMono-Regular.ttf" />
</ItemGroup>
```

If the font files are missing the dashboard still renders — `Styles.xaml` has fallbacks (Cambria for serif, Segoe UI for sans, Consolas for mono). The look will not match the approved mockups exactly until the real fonts are bundled.
