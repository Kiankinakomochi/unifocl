# Unity Plugins for Bridge

This folder holds precompiled managed plugins required by the Unity editor bridge.

## Protobuf Contract DLL

Generate and sync `Unifocl.Shared.dll` from the protobuf submodule:

```bash
./scripts/sync-protobuf-unity-plugin.sh
```

The generated DLL is copied to:

- `src/unifocl.unity/EditorScripts/Plugins/Unifocl.Shared.dll`

Unity projects receiving bridge payloads should include this plugin assembly so bridge code can compile against shared protobuf contracts.
