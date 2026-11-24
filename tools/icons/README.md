Icon font build (Assets\AgrimIcons.otf).

1. Edit mixmap.py: Segoe Fluent codepoint used in the app -> Iconify icon (prefix:name).
2. python mixfetch.py                      # downloads the SVGs into mix.json
3. dotnet run outline.cs -- .              # strokes -> filled outlines (outlines.json)
4. python buildmix.py                      # writes ..\..\Assets\AgrimIcons.otf
5. dotnet run sheet.cs -- ..\..\Assets\AgrimIcons.otf sheet.png   # preview

Sets: Hugeicons, Phosphor, Iconoir, Majesticons, Circum, Streamline Flex (all open source). Needs: pip install fonttools.
iconmap.py is only used to check every codepoint the app uses is mapped.
