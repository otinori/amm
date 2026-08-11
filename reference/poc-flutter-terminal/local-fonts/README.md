# local-fonts/

`fallback.woff2` is a minimal subset font used only to unblock Flutter Web/CanvasKit
rendering in this sandbox, which cannot reach `fonts.gstatic.com` (see `../RESULTS.md`
for why that matters). It is **not** representative of a production font bundle — it
only contains the exact characters used in this PoC's UI text and test strings
(`../subset_text.txt`), so any other CJK/symbol character will render as a missing glyph.

Regenerate with:

```bash
pip install fonttools brotli
python3 -m fontTools.subset /usr/share/fonts/opentype/unifont/unifont_jp.otf \
  --text-file=../subset_text.txt \
  --flavor=woff2 \
  --output-file=fallback.woff2 \
  --no-hinting --desubroutinize
```

A real Flutter Desktop build should instead bundle a complete CJK font (e.g. Noto Sans
CJK JP, a few MB) via `pubspec.yaml`'s `fonts:` section, the same way amm's WinForms
build already bundles xterm.js locally instead of depending on a CDN.
