# Poltergeist

screenshot and screen recording tool that uploads straight to your haunt image host. hotkey, drag, link's on your clipboard.

**download:** https://poltergeist4haunt.github.io/Poltergeist-Site/ (mac and windows builds are there)

## what's in here

- `MAC` swift app (menu bar)
- `WINDOWS` c# / wpf app (tray), has its own readme with the hotkeys and stuff
- `ref` the old puush clients i used as reference

## building it yourself

mac: `cd MAC && ./build.sh`, app ends up in `MAC/build`

windows: `cd WINDOWS && ./build.sh` (or `build.ps1` on windows), zip ends up in `WINDOWS/dist`

you need your own haunt api key to upload, grab one from the dashboard and paste it in settings.
