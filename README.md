# NSPD Repack GUI (GTA-NX NSP Repack Tool)

Rewrite of the Python/PowerShell NSPD -> NSP repacker; still drives `hacpack` + `hactool`.


## How to Use

### **GUI**: 
1. Run the exe file and select your `.nspd` folder or drag the folder onto the exe file.
2. Optionally change or replace other settings (Current prod.keys FW Version: 21.2.0)
3. Press **Start Repack**.
4. Once it finishes, press **Show NSP in folder** to go directly to your outputed `{titleid}.nsp` file. 


### **CLI**: 
`NspdRepack.exe --cli "D:\game_nx_master.nspd" [--keys f] [--out d] [--titleid id]
[--keygen n] [--sdk hex] [--no-verify] [--no-deep] [--keep-work] [--romfs d --copy-romfs]`
`--romfs d` is an optional override of the bundled RomFS (`--copy-romfs` only applies to such an override).
Exit code 0 = OK, 1 = failed, 2 = bad arguments, 130 = cancelled.
The exe is a GUI-subsystem program, so cmd / PowerShell do not wait for it. To get the output and the exit code
run it as `start /wait "" NspdRepack.exe --cli "D:\game_nx_master.nspd"` (cmd, then `echo %errorlevel%`) or
`(Start-Process .\NspdRepack.exe -ArgumentList '--cli','D:\game_nx_master.nspd' -Wait -PassThru).ExitCode` (PowerShell).

## What changed vs. the Python version

- Title ID read from `main.npdm` (override box if you want to force one)
- Real input validation up front (main, main.npdm, control data, disk space)
- Base RomFS is bundled in the exe; hacpack reads the unpacked copy in place
- NCAs identified by "what hacpack just created", not by file-size guesses
- Meta NCA is actually verified (the Python version always skipped it)
- hactool output checks on all three NCAs (content type, sections, Title ID) + in-process PFS0 check +
  optional ExeFS round-trip hash check (covers the ExeFS only; `deepVerified` in manifest.json is true only
  if that check really ran)
- Disk-space estimate includes the ExeFS / logo / control data, not just the RomFS
- Unexpected files in `program0.ncd\code` are reported before they get packed into the ExeFS
- The work folder has a short name (hacpack does not handle paths over 260 characters)
- No console window flashing, live log, cancel button, work folder cleaned after success
- Missing control data is an error; missing logo builds without a logo section and says so

## Build

Needs Visual Studio 2019/2022 or the .NET SDK (exe itself targets `net48`):

    dotnet build -c Release

Output: `bin\Release\net48\NspdRepack.exe`

## prod.keys handling

hacpack prints every `prod.keys` entry it does not use ("Failed to match key ... (value ...)"), and many of those
are console-unique (bis keys, device key, eticket RSA key pair, SSL key, sd_seed, save MAC key). To keep them out of
the log, the .log files and the exe:

- the build bundles a **filtered** copy of `prod.keys` (see the `AppendPayload` task, same pattern as `Core\KeyFilter.cs`);
- at run time the tools get a filtered copy of whichever keys file is used (also a Browse-d one), stored in the
  session folder and deleted with it;
- as a last line of defence `ToolRunner` drops any "Failed to match key" line instead of logging it.

The removed entries are exactly the ones hacpack ignores, so the output does not change.