# Unity Compatibility Check

This project compiles `src/unifocl.unity/EditorScripts` against real Unity assemblies.

Required input:
- `UnityEditorManagedDir`: path to Unity's Managed folder that contains `UnityEditor.dll`.

Optional input:
- `UnityProjectPath`: Unity project root path. When set, all DLLs from `Library/ScriptAssemblies` are referenced, including `Assembly-CSharp` and every asmdef output.

You can pass these as MSBuild properties or environment variables:
- `UNIFOCL_UNITY_EDITOR_MANAGED_DIR`
- `UNIFOCL_UNITY_PROJECT_PATH`

Example:

```bash
dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj \
  -p:UnityEditorManagedDir="/Applications/Unity/Hub/Editor/6000.2.6f2/Unity.app/Contents/Managed" \
  -p:UnityProjectPath="/absolute/path/to/YourUnityProject"
```

Notes:
- If `UnityProjectPath` is omitted, checks still validate Unity API and BCL compatibility, but cannot validate references to project-defined types.
- If `Library/ScriptAssemblies` is missing, open the project in Unity once to generate script assembly DLLs.
