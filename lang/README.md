ZyZoom Internet Downloader - language files
========================================

Each file is a plain UTF-8 text file named <code>.txt (en.txt, es.txt, ...).
The app scans this folder at startup and lists every .txt file in Settings > Language.

Format
------
One key=value per line. Lines starting with # are comments. Do not change the
keys on the left of "=". Missing keys fall back to en.txt.

The first lines are metadata:
  code=es            language code (also the file name)
  name=Español       name shown in the Language list
  dir=ltr            ltr (left-to-right) or rtl (right-to-left)

For right-to-left languages (Arabic, Hebrew, Persian, Urdu) use:
  dir=rtl

Adding a language
-----------------
1. Copy en.txt to <code>.txt.
2. Change code, name and dir.
3. Translate the values on the right of "=".
4. Save as UTF-8 and restart the app (or reopen Settings > Language).

Notes
-----
- Keep placeholders such as {n}, {id} and {name} unchanged.
- Do not put spaces around "="; leading/trailing spaces are trimmed.
- Values cannot contain line breaks.
