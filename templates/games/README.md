# Game templates

Each file describes one game that RigShift's automation can pick from a list. RigShift checks the running
processes every 2 seconds; when one of `executables` appears, the rule switches.

```json
{
  "id": "ams2",
  "name": "Automobilista 2",
  "executables": ["AMS2AVX.exe", "AMS2.exe"]
}
```

- `id`: lowercase letters, digits and `-`, unique, never changed once released (rules refer to it).
- `name`: shown in the app.
- `executables`: file names of the game's own process as Task Manager shows them under *Details* – not the launcher.

To add a game, add a file here and open a pull request. The tests check every template. If you cannot, open a
[Game template request](https://github.com/ManuelStaggl/RigShift/issues/new?template=game_template.yml).
