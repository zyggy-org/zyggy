Kind: build. Step: {{step}}.

Build step "{{step}}" of the spec {{spec}} as approved at commit {{spec_commit}}; read it with `git show {{spec_commit}}:{{spec}}`. Follow the project's own workflow for one slice: the tests first, then the code, then its verification commands. Commit the work on this branch. Do not start another step, do not change the spec, and do not merge, release or deploy.

When something is unclear, ask Central with `zyggy ask` and follow your stated default when no answer comes. End with the report the project's workflow asks for: what was built, the checks run and their result.
