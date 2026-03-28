Hydrate full scene context from the active unifocl project for agent reasoning.

Arguments (optional — pass any combination): $ARGUMENTS
  --project <path>   explicit project path
  --depth <n>        hierarchy/asset traversal depth (default: 6 for hierarchy, 4 for project)
  --limit <n>        max objects to return
  --compact          omit null/default fields to reduce token usage

Steps:

1. Dump the scene hierarchy (GameObject tree):
   `unifocl exec "/dump hierarchy --format json --depth 6 --limit 2000 $ARGUMENTS" --agentic --format json`

2. Dump the project asset structure:
   `unifocl exec "/dump project --format json --depth 4 --limit 1000 $ARGUMENTS" --agentic --format json`

3. For inspector detail on a specific object — identify its path from the hierarchy dump, then:
   `unifocl exec "/inspect <path>" --agentic --format json $ARGUMENTS`
   followed by:
   `unifocl exec "/dump inspector --format json $ARGUMENTS" --agentic --format json`

4. Summarize findings:
   - Active scene name (from hierarchy root metadata)
   - Total GameObjects, root-level objects, any disabled objects
   - Scripts and components attached to key objects
   - Any missing script references (null component entries)

Use this context before planning any `/mutate` operation so mutation paths target correct objects.
If no project is open, run `/init` first.
