Hydrate full scene context from the active unifocl project for agent reasoning.

Arguments (optional — pass any combination): $ARGUMENTS
  --project <path>   explicit project path
  --depth <n>        hierarchy/asset traversal depth
  --limit <n>        max objects to return
  --compact          omit null/default fields

Steps:

1. Dump scene hierarchy:
   `unifocl exec "/dump hierarchy --format json --depth 6 --limit 2000 $ARGUMENTS" --agentic --format json`

2. Dump project asset structure:
   `unifocl exec "/dump project --format json --depth 4 --limit 1000 $ARGUMENTS" --agentic --format json`

3. For inspector detail on a specific object:
   `unifocl exec "/inspect <path>" --agentic --format json $ARGUMENTS`
   then:
   `unifocl exec "/dump inspector --format json $ARGUMENTS" --agentic --format json`

Use this context before planning any mutation operation.
