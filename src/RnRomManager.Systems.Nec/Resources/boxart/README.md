# Default box art

`tg16.svg`, `tgcd.svg`, `pcfx.svg` and `sgx.svg` are the sources (made for this project, MIT). The embedded `tg16.png`, `tgcd.png`,
`pcfx.png` and `sgx.png` are 512×512 renders of them (spec §6.1). After editing an SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=tg16.png file:///<full path>/tg16.svg
```
