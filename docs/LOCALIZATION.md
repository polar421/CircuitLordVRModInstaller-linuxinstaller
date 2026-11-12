# Localization

The interface ships ten complete catalogs. Every application-owned label, tooltip,
dialog, notification, status and accessibility name resolves through
`TranslationCatalog`; missing entries fall back to English rather than showing a
raw key or an empty control.

## Supported languages

| Code | Native name | UI font |
|---|---|---|
| `en` | English | Liberation Serif |
| `de` | Deutsch | Liberation Serif |
| `es` | Español | Liberation Serif |
| `fr` | Français | Liberation Serif |
| `pt-BR` | Português (Brasil) | Liberation Serif |
| `it` | Italiano | Liberation Serif |
| `pl` | Polski | Liberation Serif |
| `ru` | Русский | Liberation Serif |
| `ja` | 日本語 | Noto Sans CJK JP |
| `zh-Hans` | 简体中文 | Noto Sans CJK SC |

The language dropdown stores the exact code above. Variants that are not shipped
(for example `pt-PT`, `zh-CN` or `en-US`) resolve to English rather than being
silently treated as an equivalent locale.

## Translation glossary

Technical names are not translated. This includes `WiVRn`, `OpenXR`, `Monado`,
`SteamVR`, `OpenVR`, `Proton`, `tf2vr`, `CircuitLord`, the backend tokens
`latest` and `beta`, and version strings such as `1.0.27`.

| Term | en | de | es | fr | pt-BR | it | pl | ru | ja | zh-Hans |
|---|---|---|---|---|---|---|---|---|---|---|
| Appearance | Appearance | Darstellung | Apariencia | Apparence | Aparência | Aspetto | Wygląd | Внешний вид | 外観 | 外观 |
| Theme | Theme | Design | Tema | Thème | Tema | Tema | Motyw | Тема | テーマ | 主题 |
| UI scaling | UI scaling | UI-Skalierung | Escalado de interfaz de usuario | Mise à l'échelle de l'interface utilisateur | Dimensionamento da IU | Ridimensionamento dell'interfaccia utente | Skalowanie interfejsu użytkownika | Масштабирование пользовательского интерфейса | UIのスケーリング | 用户界面缩放 |
| Reduced motion | Reduced motion | Reduzierte Bewegung | Movimiento reducido | Réduire les animations | Movimento reduzido | Movimento ridotto | Ograniczony ruch | Минимум движения | 動きを減らす | 减少动态效果 |
| Follow system | Follow system | System folgen | Seguir el sistema | Suivre le système | Seguir o sistema | Segui il sistema | Zgodnie z systemem | Как в системе | システムに従う | 跟随系统 |
| Install | Install | Installieren | Instalar | Installer | Instalar | Installa | Zainstaluj | Установить | インストール | 安装 |
| Update | Update | Aktualisieren | Actualizar | Mise à jour | Atualizar | Aggiorna | Zaktualizuj | Обновить | アップデート | 更新 |
| Reinstall | Reinstall | Neu installieren | Reinstalar | Réinstaller | Reinstalar | Reinstalla | Zainstaluj ponownie | Переустановить | 再インストール | 重新安装 |
| Stable | Stable | Stabil | Estable | Stable | Estável | Stabile | Stabilna | Стабильная | 安定版 | 稳定版 |
| Beta | Beta | Beta | Beta | Bêta | Beta | Beta | Beta | Бета | ベータ版 | Beta 版 |
| Older version | Older versions | Ältere Versionen | Versiones anteriores | Versions antérieures | Versões anteriores | Versioni precedenti | Starsze wersje | Старые версии | 以前のバージョン | 旧版本 |
| Channel | Channel | Kanal | Canal | Chaîne | Canal | Canale | Kanał | Канал | チャンネル | 频道 |
| Latest output | Latest | Neueste | Más reciente | Dernière sortie | Mais recente | Più recente | Najnowsze | Последние | 最新 | 最新 |
| Settings | Settings | Einstellungen | Configuración | Paramètres | Configurações | Impostazioni | Ustawienia | Настройки | 設定 | 设置 |
| Diagnostics | Diagnostics | Diagnose | Diagnóstico | Diagnostic | Diagnóstico | Diagnostica | Diagnostyka | Диагностика | 診断 | 诊断 |

## Formatting

- `TranslationCatalog.Format` uses `CultureInfo.CurrentCulture`; `Loc.Use` sets
  the current and default thread cultures for the selected language.
- Dates and times use short general formatting (`g`) in the current locale.
- Counts and progress percentages use the current locale's separators.
- Version strings, backend tokens, paths, raw command output, git output and
  release notes are never reformatted or translated.

## Fonts and licenses

- Liberation Serif is used for Latin and Cyrillic scripts (including Polish and
  Russian) and is distributed under its bundled license in
  `Assets/Fonts/LICENSE.txt`.
- Noto Sans CJK JP and Noto Sans CJK SC provide Japanese and Simplified Chinese
  coverage under the SIL Open Font License 1.1, reproduced in
  `Assets/Fonts/NOTO-OFL.txt`.
- The Settings → About third-party notice is translated and names both font
  families.

## Adding or changing a string

1. Add the key and English text to
   `Core/Localization/Translations/en.json`.
2. Add the same key to all nine other catalogs. Placeholders must match exactly.
3. Use the key from XAML (`{loc:Tr Key=...}`) or code (`Loc.Get` / `Loc.Format`,
   `SetStage`, `SetUpdateStatus` or `SetBackendUpdateStatus`).
4. Run:

   ```bash
   dotnet test tests/CircuitLord.VRModInstaller.Core.Tests -c Release
   ```

   The tests fail on missing/empty keys, placeholder changes, unused keys,
   missing technical identifiers, or locale-formatting regressions.

The current catalogs contain 393 keys per language. Automated tests prove key
completeness and placeholder consistency; they do not replace native-speaker
review of tone. Raw backend and git output is deliberately preserved in its
original language.
