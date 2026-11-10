# Default box art

`gamegear.svg` is the source (made for this project, MIT). The embedded `gamegear.png` is a
512×512 render of it (spec §6.1). After editing an SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=gamegear.png file:///<full path>/gamegear.svg
```
