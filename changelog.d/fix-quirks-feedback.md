---
bump: patch
---

### Fixed
- `/asset` slash commands (`/asset rename`, `/asset remove`, `/asset describe`) now route correctly instead of returning "unsupported route" — the routing condition was too narrow (only covered get/set/refresh).
- `asset rename <path> <new-name>` and `asset remove <path>` are now implemented as REPL commands; previously they appeared in the catalog but fell through with an unhelpful usage error.
- Removed `asset create` and `asset create-script` from both the slash and project command catalogs — they routed to nothing. Use `make --type <type>` instead.
- Stale session lock error now prints the exact `rm` command to clear the dead lock file, so agents don't need to hunt for the path manually.
- Documented double-quote path support in `asset get`, `asset set`, `asset rename`, `asset remove`, and `make --parent` usage strings — paths containing spaces must be wrapped in double quotes (e.g. `asset get "Assets/Asset Bundle/Foo.asset"`).
