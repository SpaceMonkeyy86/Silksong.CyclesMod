## Changelog

### v1.0.0

- Renamed to CyclesMod and published on Thunderstore and GitHub.
- Added a writeup explaining the details of load times and cycle variance.
- Removed most of the debug logging, since there was simply way too much.
- The mod now gives vanilla-accurate behavior if all settings are disabled.
- Removed Clear Memory Delay, since it was found to not do anything meaningful.

### v0.2.0

- Fixed the game sometimes freezing when loading a savestate.
- Fixed weird behavior after quitting to the main menu and then reloading a save.
- Reworked Clear Memory Delay to cause additional lag instead of simply extending the load.
- Introduced an additional fix which should get rid of the ~0.1s variance still present in cycles from v0.1.0.
- Added debug logging for exact timing of different parts of the load.

### v0.1.0

- Initial release.