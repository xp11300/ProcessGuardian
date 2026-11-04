# Process Guardian

Process Guardian is a WPF desktop application for launching selected Windows programs in isolated Job Objects. When a reliably selected top-level window closes, the application closes that program's Job Object, which asks Windows to terminate processes still assigned to it.

## Run

Open `ProcessGuardian.sln` or `ProcessGuardian.slnx` in Visual Studio and press **F5**, or run:

```powershell
dotnet run --project ProcessGuardian.csproj
```

The independent Windows integration checks are in `../ProcessGuardian.JobObjectTests`:

```powershell
dotnet run --project ../ProcessGuardian.JobObjectTests/ProcessGuardian.JobObjectTests.csproj
```

## Process and compatibility boundaries

- Each application is started suspended, assigned to its own Job Object, and resumed only after assignment succeeds. If process creation, assignment, or resume fails, startup cleanup terminates and waits for the still-suspended process.
- Windows may reject Job Object assignment, including because of host Job restrictions, incompatible existing Jobs, Windows version/policy, or security boundaries. The application reports failure and does not resume a process it could not assign.
- Descendants created normally by a jobbed process generally inherit the Job, but external brokers, services, COM servers, elevated processes, other sessions/users, shared processes, or processes explicitly created outside the Job are not guaranteed to be managed. Process Guardian does not elevate itself or forcibly terminate processes outside its Job.
- Window selection is heuristic. Hidden, titleless, owned, tool, packaged/UWP-activated, and ambiguous multi-window applications may not have a reliably identifiable main window. In that case monitoring reports unavailable and keeps the Job alive; the user can stop it explicitly or exit Process Guardian.
- A selected window closing is checked against other eligible windows before automatic cleanup. Closing one of several eligible windows does not by itself stop the application.
- The launcher accepts raw Windows command-line text after the executable name; callers must quote individual arguments for the target program's command-line parser. It does not invoke a shell.
- Cross-user or cross-session launches, UAC elevation workflows, service management, and applications that depend on separate shared/update processes are outside the supported management boundary.

The Job Object uses `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; closing its final handle terminates processes in that Job. Keep the application running in the notification area to retain management. Use **Exit** in the tray menu for orderly cleanup of managed Jobs.
