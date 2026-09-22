#if UNITY_EDITOR
// Optional-package tool categories compile into their own assemblies (skipped when the package is
// absent) and reuse the bridge's hierarchy, scene-persistence and dry-run helpers.
//
// This lives in a file of its own on purpose. /init only adds and overwrites payload files, and the
// global payload is shared by every CLI version on a machine, so an older CLI re-syncing a project
// restores its own copies of the files it knows while leaving newer add-on files in place. Keeping
// the grant out of any file an older CLI ships means it survives that re-sync, and the add-on
// assembly keeps compiling instead of breaking the project's script compilation.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UniFocl.EditorBridge.ProBuilder")]
#endif
