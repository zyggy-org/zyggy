# Spec: 55 — Central maintains its routing knowledge and its own settings

## Status

- 2026-11-18: spec written. Not approved.
- 2026-11-19: approved by the owner, including the three decisions beyond the interview (the scan's exclude list is a fifth settable key; every node applies the five keys while running; outbound folders lie in the `business/` part of memory).

## Goal

**As** the owner, **I want** to tell Central in a conversation which kind of problem goes to which agent, project and machine, and where a project lives, **so that** Central keeps that itself — the routing knowledge in its memory, the settings in its own `node.json` — and I never edit a file or look up a path.

It also closes the gap left by the v0.23.0 release: the supervisor run of Central supervision does not start on Central, because Central's own project cannot be published without also publishing the service user's home folder.

## Context

- **Roadmap entry**: `_plans/ROADMAP.md` #55 (added 2026-11-18, first in the build order, before the remaining slices of Central supervision).
- **Founding-spec sections**: §3 Components (Central, Cli), §4 Registry file, §6 Pre-flight step 1, §7 Memory (inbox, dream, `get_context`), §8 Work boundary and Injection, §9 design rules, §10 `node.json`, §11 Runbooks.
- **Reference feature**: the discovery registry (`_specs/14-discovery-registry-routing.md`) with its follow-up for nested folders (`_specs/45-discovery-nested-folders.md`), because this feature changes one discovery rule and adds a verb beside `zyggy discover`. For the file write, `zyggy setup` is the closer model: it completes `node.json` through a temporary file, validates it with the reader, keeps a backup and then replaces the original.
- **Owner's answers (interview of 2026-11-18)**: scope is the routing knowledge plus `supervision.project`, `devRoots`, `supervision.workMachines` and `supervision.outboundPaths`; no permission prompt, Central applies what he says and tells him afterwards; never from an unattended run; the routing knowledge lives in Central's memory, no new file format; Central changes its own `node.json` only and gives the exact line for a laptop.
- **Facts verified in the code and the template on 2026-11-18** (files read):
  - `node.json` is read once when the node starts; nothing re-reads it. On Central the node is restarted only by the VM administrator, and Central's session runs as the service user without that right. A changed file therefore has no effect today until someone else restarts the node.
  - `discovery.exclude` is checked only before the scan descends into a folder. A folder that holds `.claude/` is published whatever its name. The 45 spec states this on purpose ("a project's own name is never checked against this list").
  - A `devRoots` entry itself is never a project; only folders below it are. So Central's own working directory can only be published by listing its parent, which on Central also holds the service user's home folder.
  - `supervision.project` is a registry name, resolved on every pass against the node's own last scan. Absent or unknown: one Warning, no supervisor run.
  - An unattended run is recognised by `ZYGGY_HOOKS=off`, which the job runner, the supervisor wake, the dream and the timers set. `zyggy mandate grant` and a plain `zyggy submit` already refuse on it.
  - The Central template denies the session both reading and editing `~/.config/zyggy/**` (two pinned deny rules), and the `discover` skill says "Never edit the registry file or `node.json` yourself; the owner changes `devRoots`."
  - The dream pass never writes `agents.md`, and `get_context` never returns it. A `[stated]` inbox line is never dropped; the dream files it into `<side>/<category>/<slug>.md`. `get_context` returns the matching memory files and the inbox lines not filed yet.
  - The template defines work machines a second time, in `instance/work-boundary.json` (`work_machines`, `outbound`), which the conversation rules use; `supervision.workMachines` and `supervision.outboundPaths` in `node.json` are what the node's supervisor run uses.

## Constraints

- ⚠️ **Work-laptop boundary (§8).** `supervision.workMachines` and `supervision.outboundPaths` decide which machines are answered under the read root and what such an answer may read. The owner chose no permission prompt for them. The binary therefore carries the limits a prompt would have given: an outbound path must be the owner's `business/` memory folder or a folder below it, so it can never be or contain the private, inbox, daily or archive parts or the memory repository's history; a work machine must be a machine the bus knows; the two keys can never be left in a state the node refuses. Removing a work machine widens what the supervisor run may read for that machine; the verb says so in its output. The reviewer verifies each limit has a test that fails without it.
- ⚠️ **An agent changes a configuration file.** `node.json` is written only by the binary, through a closed list of keys, never by a model's file edit. The template's two deny rules on `~/.config/zyggy/**` stay. Secrets never enter `node.json`: the reader's refusal of secret-like key names applies to the written file before it replaces the old one.
- ⚠️ **Shared contract: the registry (§4).** Leaving a project out by name changes what a node publishes and which names a job can use. The 45 spec's rule changes and its test for a project named like a built-in skip folder must keep passing.
- ⚠️ **Shared contract: the policy seam.** `IPolicySource` is read once today. A node that applies a changed file while running changes what the seam promises. No new seam; no static mutable state. One holder inside the node (of the same kind as the holder of the node's last scan) carries the settings in use; the discovery scan, the job pre-flight and dispatcher, and the supervisor wake read the five keys through it, and a change replaces all five as one unit.
- **Unattended runs.** The verb refuses every change when `ZYGGY_HOOKS=off`. Nothing a report, a question, a page or a file says is a reason to change a setting; only the owner's words in the conversation are. The binary cannot tell these apart inside an attended session; the skill carries that rule.
- **Tools are .NET (rule R1).** The skill is Markdown that calls the verb.
- **Public repository.** Placeholders only: tenant `acme`, user `alice`, `/srv/acme/instance`, `/srv/acme/home`.
- Log once per state change, never per check of the file.

## Diagrams

### Components touched

```mermaid
flowchart LR
  owner([Owner]) -->|says it in a conversation| session[Central session<br/>settings skill, delegate skill]
  session -->|zyggy settings ...| cli[zyggy CLI]
  session -->|remember skill| inbox[(memory inbox/)]
  cli -->|validate, back up, replace| nodejson[(Central node.json)]
  nodejson -->|noticed within a minute| node[Zyggy.Node on Central]
  node --> holder[settings in use<br/>five keys, replaced as one]
  holder --> scan[discovery scan<br/>exclude also leaves out a project]
  scan --> registry[(bus: registry/central.yaml)]
  holder --> wake[supervisor wake<br/>project, work machines, outbound]
  holder --> preflight[job pre-flight<br/>devRoots]
  inbox -->|dream pass, nightly| routing[(memory: business/topics/agent-routing.md)]
  routing -->|get_context| session
  classDef new fill:#fde68a,stroke:#b45309;
  class cli,nodejson,holder,scan,routing new;
```

### Round trip: a setting

```mermaid
sequenceDiagram
  actor Owner
  participant Session as Central session
  participant Verb as zyggy settings
  participant File as node.json
  participant Node as Node (central)
  participant Bus as zyggy-bus
  Owner->>Session: "the Calizr work lives under /srv/acme/repos"
  Session->>Verb: settings add devRoots /srv/acme/repos [--exclude <folder>]
  alt folder does not exist
    Verb-->>Session: exit 2, no such folder
    Session->>Owner: asks for the exact path
  else valid
    Verb->>File: temp file, reader check, backup, replace
    Verb-->>Session: before -> after, and the projects the root adds
    Session->>Owner: tells what changed
    Node->>File: notices the change
    Node->>Node: re-reads the five keys, scans
    Node->>Bus: registry commit when the content differs
  end
```

### Round trip: routing knowledge

```mermaid
sequenceDiagram
  actor Owner
  participant Session as Central session
  participant Memory as Central memory
  participant Dream as Dream pass
  Owner->>Session: "booking bugs go to the env-debugger agent in calizr on the home laptop"
  Session->>Memory: remember "Routing: ..." (inbox line, [stated])
  Session->>Owner: tells what it kept
  Dream->>Memory: files the line into business/topics/agent-routing.md
  Owner->>Session: later: "there is a booking bug"
  Session->>Memory: get_context "agent routing"
  Memory-->>Session: the routing lines (filed, and not yet filed)
  Session->>Owner: proposes machine, project, agent; submits after his go
```

### Refusals of the verb

```mermaid
flowchart TD
  start[zyggy settings set / add / remove] --> un{ZYGGY_HOOKS=off?}
  un -- yes --> r5[exit 5: refused in an unattended run]
  un -- no --> central{machine is central?}
  central -- no --> r3[exit 3: only on Central]
  central -- yes --> key{key in the closed list?}
  key -- no --> r2a[exit 2: unknown setting]
  key -- yes --> value{value passes its checks?}
  value -- no --> r2b[exit 2: one line naming the check]
  value -- yes --> whole{whole file passes the reader?}
  whole -- no --> r3b[exit 3: file left as it was]
  whole -- yes --> ok[backup, replace, print before -> after, exit 0]
```

### Slice map

```mermaid
flowchart LR
  s1[1 exclude leaves out a project<br/>Fake] --> s2[2 supervisor starts on a Central-shaped tree<br/>Wire]
  s2 --> s3[3 settings verb rules<br/>Fake]
  s3 --> s4[4 settings verb on a real file<br/>Wire]
  s4 --> s5[5 node applies a changed file<br/>Fake]
  s5 --> s6[6 changed setting reaches registry and wake<br/>Wire]
  s6 --> s7[7 routing knowledge kept and read back]
  s7 --> s8[8 Central template: skills and rules]
```

## Behavior

### Discovery: leaving a project out by name

A folder whose name matches a glob of the owner's `discovery.exclude` is neither published as a project nor descended into. The built-in skip list (`node_modules`, `bin`, `obj`, …) keeps its present meaning: never descended into, but a project with such a name is still found. A project left out this way is unknown to the node: a job that names it gets `unknown_project: unknown name`. `zyggy discover --dry-run` shows the result as before; no new notice.

With that, Central publishes its own working directory by listing the parent in `devRoots` and the service user's home folder in `discovery.exclude`, and `supervision.project` names the published project. `supervision.project` stays a registry name.

### CLI

`zyggy settings show [--config <path>]` — prints the five settings below as they stand in the file, one line per key (`<key>: <value or values, or (none)>`). Works on every machine and in every run, also an unattended one. Exit 0; exit 3 when the file is refused.

`zyggy settings set supervision.project <name>`
`zyggy settings add <key> <value>` and `zyggy settings remove <key> <value>` for the list keys
`zyggy settings add devRoots <path> --exclude <glob>` (the option may repeat) writes the root and the exclude globs in one write, so the node never sees the root without them.

The three changing forms take `--config <path>` and behave alike:

- refused with exit 5 and `zyggy: refused: settings are changed only in an attended session` when `ZYGGY_HOOKS=off`;
- refused with exit 3 and `zyggy: settings are changed by this verb only on Central` when the file's `machine` is not `central`;
- exit 2 with one line naming the check for an unknown key, a wrong form for the key (`set` on a list, `add` on `supervision.project`) or a value that fails its check;
- on success, one line per changed key: `settings: <key>: <before> -> <after>`, then `settings: the node applies this within a minute`; a value that is already there prints `settings: <key>: unchanged` and writes nothing; exit 0;
- `add devRoots` also prints the projects a scan finds under the new root with the resulting settings: `settings: publishes: <name>, <name>` (or `settings: publishes: nothing`), so Central can tell the owner what became visible;
- a change after which a scan with the resulting file no longer finds the `supervision.project` project is refused (exit 2, `supervision.project <name> would no longer be found`); this covers removing its root and excluding its folder;
- the write: a temporary file beside the original, checked by the node's own reader, a backup of the original (named and placed as `zyggy setup` names its backups; never removed by the verb), then the replacement. Every other key, and their order, stays as it was. A file that would not pass the reader is never put in place (exit 3, the original untouched);
- exit 3 thus has two causes, told apart by the message: not Central, or a file the reader refuses.

There is no undo form: a wrong change is reversed with the opposite command, and the backup holds the earlier file.

The closed list of keys and the check each value must pass:

| Key | Form | Check |
| --- | --- | --- |
| `supervision.project` | `set` | a project name that a scan with the file's settings finds on this machine; otherwise `no such project on this machine: <name>` and the names that exist |
| `devRoots` | `add`, `remove` | an absolute path of an existing folder; otherwise `no such folder: <path>` |
| `discovery.exclude` | `add`, `remove` | a non-empty folder-name glob without a path separator, matched as the scan matches it |
| `supervision.workMachines` | `add`, `remove` | a machine name with a registry file of this tenant in the bus checkout as it stands (the verb does not pull), and not `central`; otherwise `no such machine on the bus: <name>` (a machine that never published cannot be added yet); `add` is refused while `supervision.outboundPaths` is empty: `name an outbound folder first` |
| `supervision.outboundPaths` | `add`, `remove` | an existing folder that, after links are resolved, is the `business/` folder of the owner's memory (`<ZYGGY_MEMORY_ROOT>/<tenant>/<user>/business`, resolved as the memory verbs do) or a folder below it; anything else, including the memory root, the owner's memory folder itself and `private/`, is refused: `outbound folders lie under the business memory`; removing the last one is refused while `supervision.workMachines` is not empty |

`supervision.workMachines remove` prints one more line: `settings: <machine> is no longer a work machine: its questions are answered without the read root`.

`discovery.exclude` is on the list although the owner named four settings: `devRoots` cannot be used on Central without it.

### The node applies a changed file

Every node looks at its `node.json` (size and time of last change) at most twice a minute. When it changed, the node reads the file with the same reader as at start:

- **Valid**: the values of `devRoots`, `discovery.exclude`, `supervision.project`, `supervision.workMachines` and `supervision.outboundPaths` replace the ones in use, together. One Information line names the keys that changed, never a value of the work-boundary keys beyond a count. When `devRoots` or `discovery.exclude` changed, a discovery scan runs right away and publishes as usual. A supervisor run already started keeps the values it started with. Job pre-flight uses the values in use when it checks a job: after a root is added, a job that names a path under it is accepted; after a root is removed, such a job is answered `unknown_project: outside devRoots`.
- **Another key changed too**: one Warning naming those keys and that they need a restart of the node. They keep their old value.
- **Refused by the reader**: one Warning with the reader's line; every value in use stays. One Information line when a later change is valid again. The node never stops because of a file changed while it runs.

Start-up is unchanged and keeps its existing tests: an invalid file at start still refuses the start.

### Routing knowledge

The routing knowledge is what the owner says about which kind of problem goes to which agent, project and machine. Central keeps it with the existing `remember` path, as one sentence that starts with the word `Routing:` and names the kind of problem, the machine, the project and the agent, for example `Routing: booking bugs go to machine home-laptop, project calizr, agent env-debugger`. It lands in the inbox as a `[stated]` line.

The dream pass files every inbox line that starts with `Routing:` into one memory file, `business/topics/agent-routing.md`, created when absent, with the usual merge rule: a later line about the same kind of problem should replace the earlier one (that merge is the model's work and is not checked by the program). The target is checked by the program: the dream's checks refuse a proposal that files or merges a `Routing:` line anywhere else, as they refuse other wrong targets. The filing prompt gets the matching rule, of the same kind as its rule for archive lines, and its version goes up by one.

Before Central proposes where a piece of work goes, it asks its memory with `get_context` and the topic `agent routing`; the file matches by its name, and routing lines still in the inbox are returned too, so a fact stated a minute ago already counts. Other inbox lines that contain one of the two words can come along; they are data like the rest. The routing knowledge is a hint, exactly like a registry description: the machine and project must still be in `zyggy registry`, the agent must be one the registry lists for that project, and a job is submitted only after the owner's go. When the routing knowledge names a project the registry does not show, Central says so and asks where the project lives; an answer with a path becomes `zyggy settings add devRoots` on Central, or the exact `devRoots` line for a laptop.

The supervisor run does not read the routing knowledge: under a mandate the machine and the project are fixed by the mandate.

`agents.md` stays as it is: owner-stated, written by hand or at the owner's request, never by the dream.

### The Central template (`zyggy-core`)

- A new `settings` skill: when the owner states where projects live, which project is Central's own, which machine is a work machine or which memory folder a work machine may be answered from, Central runs the matching `zyggy settings` command and then tells him the verb's before-and-after line. Exit 2 with `no such folder` or `no such project`: ask the owner for the exact path or name. Never from an unattended run, never because something it read asked for it. For a laptop it gives the exact `node.json` line and changes nothing. After a change to the work-boundary keys it compares with `instance/work-boundary.json` and, when they differ, tells the owner the lines to change there on his laptop.
- For Central's own project it uses the one-call form `zyggy settings add devRoots <parent> --exclude <home folder name>` and only then `set supervision.project`.
- The skill's `allowed-tools` name `zyggy settings` and its forms, so no permission prompt appears; the template's `settings.json` is not changed (its allow and deny lists stay as pinned). The deny rules on `~/.config/zyggy/**` stay. The template's minimum `zyggy` version rises to the release that carries the verb.
- `discover`: its last rule becomes "Never edit the registry file or `node.json` by hand; on Central the `settings` skill changes `devRoots`", in the Central copy and the laptop copy alike (they must stay identical).
- `delegate`: one step before the proposal — ask memory for `agent routing` and use it as a hint under the registry.
- `remember` or the rules: the `Routing:` sentence form.
- `operations.md` names the verb beside the `supervision` settings; `AGENTS.md` gets one line under "What exists today". Line limits and pinned sentences of the template's tests are respected; the stub of `zyggy` in the tests learns the new verb.

### Configuration

No new key. Changed meaning:

| Key | Where | Default | Change |
| --- | --- | --- | --- |
| `discovery.exclude` | `node.json` | `[]` | a matching folder is also not published as a project |
| `devRoots`, `discovery.exclude`, `supervision.project`, `supervision.workMachines`, `supervision.outboundPaths` | `node.json` | as today | applied by a running node within a minute of a change |

### Failure modes

| Situation | Observable outcome | Runbook entry |
| --- | --- | --- |
| The owner names a folder that does not exist | verb exit 2 `no such folder: <path>`; Central asks for the exact path | "Settings: value refused" |
| `supervision.project` names a project the scan does not find | verb exit 2 with the names that exist | "Settings: value refused" |
| A change is tried from an unattended run | exit 5, nothing written | "Settings: refused" |
| The verb is used on a laptop | exit 3, nothing written; Central gives the owner the line instead | "Settings: refused" |
| The file was edited by hand into a state the reader refuses | the running node keeps its values, one Warning; `zyggy settings` exits 3 naming the key | "Settings: node.json refused while running" |
| A hand edit changes a key the node cannot apply while running | one Warning naming the keys | "Settings: restart needed" |
| A wrong change went in | the opposite command reverses it; the backup beside the file holds the earlier state | "Settings: reverse a change" |
| The supervisor run still does not start | the existing Warning; `zyggy settings show` and `zyggy registry --machine central` show why | "Supervisor does not start" (rewritten: the settings skill, no restart) |

The runbook entries are rows of the Cli README's troubleshooting sections (where "Supervisor does not start" lives today, with "restart the node" as its remedy); the founding spec's §11 gets their titles only.

### Edge cases

| Case | Expected behavior |
| --- | --- |
| A project named like a built-in skip folder (`out`) directly under a root | still published, as in the 45 spec |
| A glob in `discovery.exclude` matches a folder with projects below it | not descended into, as today |
| `node.json` holds comments | the written file loses them; the backup keeps them (as `zyggy setup`) |
| Two changes shortly after each other | each is its own write; the node may apply them one after the other. The one-call form exists for the case where the state in between must never be seen (a root and its excludes) |
| `discovery.naming: path` | the exclude glob still matches a folder's own name, not the joined project name |
| An outbound path given through a symbolic link that leaves the business memory | refused: the check uses the resolved path |
| The memory folder variables are absent | `supervision.outboundPaths add` exits 3 naming the variable; other keys work |
| A routing sentence names a machine that does not exist | kept as stated; when used, Central finds no such machine in the registry and says so |

## Decisions

| Item | Verdict | Why |
| --- | --- | --- |
| Supervisor start on Central: leave a project out by name, or let `supervision.project` name a folder | **reshape discovery** (leave out by name) | The owner put `devRoots` in scope, so Central will list folders to scan, and the home folder must stay out of the registry in every case. A folder form of `supervision.project` would add a second way to name a project and fix only the supervisor. |
| `discovery.exclude`: "a project's own name is never checked" (45 spec) | **reshape** | Holds for the built-in list, which exists to skip build output. For the owner's own list, "exclude" that still publishes the folder is the surprise that caused the v0.23.0 incident. |
| `node.json` read once at start (§10, `IPolicySource`) | **reshape** | Central cannot restart its node, and the owner asked not to do anything by hand. Five keys are applied while running; everything else still needs a restart. |
| A model edits the file directly | **refused** | Roadmap design rule: written through the binary. A closed key list and value checks replace the prompt the owner chose not to have. |
| Permission prompt for the work-boundary keys | **none** (owner's decision) | Recommended otherwise; replaced by the binary's limits listed under Constraints. |
| Routing knowledge in `agents.md` | **keep `agents.md` owner-stated** | The dream may not write it and `get_context` does not return it; a filed memory file needs neither rule changed. |
| A new store or file format for routing | **none** (owner's decision) | `remember`, the dream and `get_context` exist; one filing rule makes the file predictable. |
| `zyggy dream agents` (§7 step 5, reserved) | **defer**, unchanged | Not needed for this. |
| An undo form of the verb | **defer** | The opposite command and the backup cover it; an undo needs rules for repeated use and for restoring values the checks would refuse. |
| A `/health` field showing the settings in use | **defer** | The health field list is a shared contract; the node's log line and the registry show the effect. |
| Changing a laptop's `node.json` from Central | **defer** (owner's decision) | Central gives the line. |
| Keeping `instance/work-boundary.json` in step automatically | **defer** | Central never commits its working directory; the skill reports the difference. |

No new package.

**Founding-spec wording proposed** (pasted by the main session after approval; each edit listed then):

- §4 Registry file, after the sentence on the scan: "A folder whose name matches the owner's `discovery.exclude` is neither published nor searched."
- §10, after the `node.json` example: "A running node applies a change of `devRoots`, `discovery.exclude`, `supervision.project`, `supervision.workMachines` and `supervision.outboundPaths` within a minute; every other key needs a restart. On Central these five are changed with `zyggy settings`, in an attended session only."
- §8 Work boundary: "`zyggy settings` accepts an outbound folder only at or below the owner's `business/` memory folder."
- §7 Dream: "An inbox line that starts with `Routing:` is filed into `business/topics/agent-routing.md`; the checks refuse another target."
- §11 Runbooks: the titles of the Failure-modes table's entries.

## Acceptance criteria

| # | Criterion | Check |
| --- | --- | --- |
| AC-1 | Given a root with a project folder `home` and `discovery.exclude: ["home"]`, the scan publishes no project `home`; without the exclude it does | Unit test on the scanner |
| AC-2 | A project named like a built-in skip folder directly under a root is still published | Unit test (the 45 spec's existing case stays green; seen failing when the built-in list is applied to projects) |
| AC-3 | On a Central-shaped tree (parent in `devRoots`, instance folder and home folder below it, home excluded, `supervision.project` naming the instance project) the node publishes exactly the instance project, and a question on the bus starts one supervisor run in that folder | Integration test through the Node host, bare repo, fake `claude` |
| AC-4 | Each key accepts a valid value and refuses each invalid one with exit 2 and its line: missing folder, relative path, unknown project, glob with a separator, unknown machine, `central` as work machine, work machine before an outbound folder, last outbound folder while a work machine is listed, a change after which the `supervision.project` project is no longer found (its root removed; its folder excluded), and as outbound folder: the memory root, the owner's memory folder itself, its `.git`, `private/`, `inbox/`, `daily/`, `archive/`, a folder outside memory, and a link under `business/` that leaves it. Absent memory variables: exit 3 for that key only. Removing a work machine prints the read-root line | Unit tests, one case per refusal |
| AC-5 | A change rewrites only the named key: every other key and value of the file is byte-for-byte the same value afterwards, a backup holds the previous file, and a second identical call prints `unchanged` and writes nothing | Unit test over a file with every section present |
| AC-6 | A result the reader would refuse (including a key named like a secret) never replaces the file; exit 3 | Unit test |
| AC-7 | With `ZYGGY_HOOKS=off` every changing form exits 5 and the file is unchanged; `show` still prints the five keys with their values and exits 0, and exits 3 on a file the reader refuses | Integration test through the real `zyggy` entry point |
| AC-8 | With `machine` other than `central` every changing form exits 3 and the file is unchanged | Integration test |
| AC-9 | `zyggy settings add devRoots <existing folder> --exclude home` then `set supervision.project <name>` on a real file: root and exclude land in one write, exit 0, output shows before and after and `publishes:` with the instance project and without `home`; `add devRoots <missing folder>` exits 2 with `no such folder` | Integration test |
| AC-10 | The READMEs of Cli, Node and Core document the verb, its exit codes and the runbook rows of the Failure-modes table | Hygiene test on the READMEs, seen failing before the rows exist |
| AC-11 | A running node whose file changes validly uses the new five values within the check period, logs one line naming the changed keys, and logs nothing more while the file stays the same | Unit test with the fake clock |
| AC-12 | A change the reader refuses leaves every value in use and logs one Warning for the streak, and one Information line when a later change is valid again; a change to another key logs one Warning naming it and does not apply it | Unit test with the fake clock |
| AC-12b | After a valid change, the scan uses the new roots and excludes, and job pre-flight accepts a path under an added root and answers `outside devRoots` for a path under a removed one | Unit test |
| AC-13 | With the node running as Central and no project published, `zyggy settings add devRoots <parent> --exclude home` and then `set supervision.project` lead, without a restart, to a registry file that holds the instance project and never, in any commit, a project `home`, and to a supervisor run for the next question | Integration test through the Node host and the real verb |
| AC-14 | A supervisor run that started before a change of the work-boundary keys keeps the read root it started with; the next run uses the new one | Integration test |
| AC-15 | A dream proposal that files a `Routing:` inbox line into `business/topics/agent-routing.md` is applied and the file holds the line; a proposal that files it anywhere else is refused by the checks. The filing prompt carries the rule and its version is raised | Unit tests on the dream's checks and applier with a prepared proposal; the prompt's test |
| AC-16 | `get_context` with topic `agent routing` returns the lines of the file as the applier of AC-15 wrote it, and a `Routing:` line still in the inbox | Unit test |
| AC-17 | The READMEs of Node and Core document the changed `discovery.exclude` rule and the five keys applied while running | Hygiene test on the READMEs, seen failing before the rows exist |
| AC-18 | The template: the `settings` skill exists with its `allowed-tools`, names the unattended and data rules and the ask-for-the-path step; `discover` (both copies) and `delegate` carry the new sentences; `settings.json` is unchanged; the minimum `zyggy` version is the release's; line limits hold | The template's bats tests in the container |
| AC-19 | On the real Central, the owner says in a conversation where Central's project lives; Central changes the settings without a prompt and reports them; `zyggy registry --machine central` shows the instance project and not the home folder; after a mandate job's report or a question, the node's log shows a supervisor run | Owner-run at release |
| AC-20 | On the real Central, the owner states one routing rule, and in a new conversation a matching request is proposed to that machine, project and agent | Owner-run at release |

## Slices

| # | Slice | Criteria | Review check |
| --- | --- | --- | --- |
| 1 | ⚠️ A folder the owner excludes is no longer published as a project (Fake) | AC-1, AC-2 | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~DiscoveryScanner"` |
| 2 | The supervisor run starts on a Central-shaped tree (Wire) | AC-3 | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~CentralOwnProject"` |
| 3 | ⚠️ `zyggy settings` accepts and refuses each value by its rule and rewrites only that key (Fake) | AC-4, AC-5, AC-6 | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Settings"` |
| 4 | ⚠️ The owner's setting lands in Central's `node.json` through the real verb; unattended and off-Central are refused (Wire; the verb's README rows) | AC-7 – AC-10 | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SettingsVerb"` |
| 5 | ⚠️ A running node applies a changed file to its scan, its job pre-flight and its supervisor wake (Fake) | AC-11, AC-12, AC-12b | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~SettingsInUse"` |
| 6 | ⚠️ A setting changed with the verb reaches the registry and the supervisor run without a restart (Wire; README rows; founding-spec wording) | AC-13, AC-14, AC-17 | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SettingsApplied"` |
| 7 | Routing knowledge is filed into one memory file and read back | AC-15, AC-16 | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Routing"` |
| 8 | ⚠️ Central's template: the `settings` skill, `discover`, `delegate`, the rules (separate repository, its own commit) | AC-18 | the template's bats suite in the container |

AC-19 and AC-20 are checked at the release, on Central.

## Out of scope

- Changing a laptop's `node.json` from Central; any other `node.json` key through the verb.
- A permission prompt, a second confirmation, or a mail about a change.
- Writing `instance/work-boundary.json`, `agents.md`, `.claude/` or the registry file.
- An undo form of the verb.
- A `/health` field, a `zyggy dream agents` step, a routing store outside memory, routing inside the supervisor run.
- A folder form of `supervision.project`.
- The remaining slices of Central supervision (what waits for the owner, mail, session tasks, the away run) and the protocol copy in `zyggy-core`.

## Open questions

None that blocks the build. Three decisions above go beyond the owner's interview answers and are his to confirm with the approval: `discovery.exclude` joins the settable keys; every node (laptops too) applies the five keys while running; the outbound folders are limited to the `business/` part of his memory. One reviewer pass (2026-11-18, the spec critic) is worked in: the outbound limit, the one-call form for a root with its excludes, the dream check on the routing file's place, no undo form.
