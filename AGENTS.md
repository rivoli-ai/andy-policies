# Agent Operating Contract

This repository is changed by several agents and people at the same time.

<!-- coord:begin -->
## Coordination

Several agents and people change this repository at the same time. Coordination runs through
`coord`, a local service. It is not paperwork about the work: it is how you get a place to work
and how you find out what everybody else is doing.

Read `docs/coordination/README.md` once. Then, every session:

```bash
coord register --agent <your-id>     # opus5, codex, kimik3, and so on. Once per session.
coord next                           # what to work on. Never empty while work exists.
coord start <target> --next "..."    # your claim, your branch, your worktree, in one call.
```

### The rules that are enforced, not requested

- **You may not commit in the primary checkout, on a protected branch, or in a worktree you do
  not hold.** A pre-commit hook checks this. Run `coord start` and work in the directory it
  gives you.
- **Two agents cannot hold one issue.** `coord start` either gives you the claim or tells you
  who has it. Do not work around a refusal; take something else from `coord next`.
- **An issue does not close without evidence.** `coord close_issue` requires the commit or pull
  request, the command you ran, and its result. Anything closed another way is detected and
  queued for audit.

### The rules that depend on you

- **Keep your claim current.** `coord update --next "..."` whenever scope, ownership, a blocker
  or the integration order changes. Updates renew your lease. During long work, call
  `coord heartbeat` before the configured lease expires (30 minutes by default). Other agents read
  your next action to decide whether to wait for you or route around you.
- **A blocker is not an idle state.** Record it with `coord update --status blocked --blockedOn
  "..."`, then call `coord next` and take unblocked work on a surface that does not overlap.
- **Never idle while work exists.** After finishing, handing off, or blocking anything, call
  `coord next` immediately. It ranks open issues first, then unreviewed pull requests, then
  unverified closures, then stale sweep areas, then orphaned worktrees. Only when it returns
  nothing is there nothing to do.
- **Stay inside your claimed surface.** Expand the claim before expanding the change. Stage
  explicit paths; never `git add -A` in a checkout that could hold another lane's work.
- **Mirror decisions to the issue.** The board is live state, the GitHub issue is the durable
  record. Any ownership decision, blocker or changed integration order belongs on both.
- **Do not merge, push, close, or release unless you have been given integration authority.**
  Report a branch and a commit to whoever has it.

### What every tool result tells you

Every `coord` call returns the live board and everything that changed since your last call. You
do not need to poll and you should not: if you have nothing to do, `coord next` will find you
something, and `coord wait` blocks properly when it genuinely cannot.
<!-- coord:end -->
