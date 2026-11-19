# Working on this repository

## Finishing a change

Every change ends with these steps, in this order, without waiting to be asked:

1. **Build**: `dotnet build src/GoogleAgendaTray/GoogleAgendaTray.csproj -c Debug` with
   no errors or warnings (`build.ps1` publishes to `dist\` and restarts the running app;
   run it only when the change should reach the installed copy).
2. **`/code-review --fix`** with this repository as an explicit target, reviewing only
   the uncommitted diff, and told not to commit or push. New files need `git add -N`
   first, or they are not in the diff. Read what the fix changed before accepting it.
3. **`/security-review`** on the same diff, every time — not only when the change
   "looks security-related". This app holds OAuth refresh tokens and a client secret,
   opens URLs that come from calendar events, and the repository is public.
4. **Build again**: the review changed code.
5. **Commit and push.** Before the push, check that the history carries no client,
   token or personal address (the author is the GitHub noreply address).

## Testing

- Never test against the real state in `%LOCALAPPDATA%\google-agenda-tray`. Set
  `GOOGLE_AGENDA_TRAY_HOME` to a throwaway folder: the copy runs as a separate instance
  next to the real one. Delete that folder when done, especially if it holds a copy of
  the real client or accounts.
- Never leave a test fixture (a fake `client.json`, say) in the real app folder.
