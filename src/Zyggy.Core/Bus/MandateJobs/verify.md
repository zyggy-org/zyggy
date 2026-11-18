Kind: verify. Step: {{step}}.

Check step "{{step}}" of the spec {{spec}} as approved at commit {{spec_commit}} against the code on this branch; read the spec with `git show {{spec_commit}}:{{spec}}`. Run the project's independent check for a slice: its slice verifier, the build and the tests. Judge each acceptance criterion of the step as met or not met, with the command and its result as evidence. Change no file and commit nothing.

Report done when the check ran, whatever it found, and list every criterion of the step with met or not met.
