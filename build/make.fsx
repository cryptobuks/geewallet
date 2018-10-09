#!/usr/bin/env fsharpi

open System
open System.IO
open System.Linq
#load "Infra.fs"
open FSX.Infrastructure

type Frontend =
    | Console
    | Gtk
    override self.ToString() =
        sprintf "%A" self

let CONSOLE_FRONTEND = "GWallet.Frontend.Console"
let GTK_FRONTEND = "GWallet.Frontend.XF.Gtk"
let DEFAULT_SOLUTION_FILE = "gwallet.core.sln"
let LINUX_SOLUTION_FILE = "gwallet.linux.sln"

type BinaryConfig =
    | Debug
    | Release
    override self.ToString() =
        sprintf "%A" self

let rec private GatherTarget (args: string list, targetSet: Option<string>): Option<string> =
    match args with
    | [] -> targetSet
    | head::tail ->
        if (targetSet.IsSome) then
            failwith "only one target can be passed to make"
        GatherTarget (tail, Some (head))

let buildConfigContents =
    let buildConfig = FileInfo (Path.Combine (__SOURCE_DIRECTORY__, "build.config"))
    if not (buildConfig.Exists) then
        Console.Error.WriteLine "ERROR: configure hasn't been run yet, run ./configure.sh first"
        Environment.Exit 1

    let skipBlankLines line = not <| String.IsNullOrWhiteSpace line
    let splitLineIntoKeyValueTuple (line:string) =
        let pair = line.Split([|'='|], StringSplitOptions.RemoveEmptyEntries)
        if pair.Length <> 2 then
            failwith "All lines in build.config must conform to format:\n\tkey=value"
        pair.[0], pair.[1]

    let buildConfigContents =
        File.ReadAllLines buildConfig.FullName
        |> Array.filter skipBlankLines
        |> Array.map splitLineIntoKeyValueTuple
        |> Map.ofArray
    buildConfigContents

let GetOrExplain key map =
    match map |> Map.tryFind key with
    | Some k -> k
    | None   -> failwithf "No entry exists in build.config with a key '%s'." key

let prefix = buildConfigContents |> GetOrExplain "Prefix"
let libInstallPath = DirectoryInfo (Path.Combine (prefix, "lib", "gwallet"))
let binInstallPath = DirectoryInfo (Path.Combine (prefix, "bin"))

let launcherScriptPath = FileInfo (Path.Combine (__SOURCE_DIRECTORY__, "bin", "gwallet"))
let mainBinariesPath = DirectoryInfo (Path.Combine(__SOURCE_DIRECTORY__, "..",
                                                   "src", CONSOLE_FRONTEND, "bin", "Release"))

let wrapperScript = """#!/bin/sh
set -e
exec mono "$TARGET_DIR/$GWALLET_PROJECT.exe" "$@"
"""

let rootDir = DirectoryInfo(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let PrintNugetVersion () =
    let nugetExe = Path.Combine(rootDir.FullName, ".nuget", "nuget.exe") |> FileInfo
    if not (nugetExe.Exists) then
        false
    else
        let nugetProc = Process.Execute (sprintf "mono %s" nugetExe.FullName, false, true)
        let firstChunk = nugetProc.Output.First()
        match firstChunk with
        | StdOut stdOut ->
            Console.WriteLine stdOut
            true
        | StdErr stdErr ->
            Process.PrintToScreen nugetProc.Output
            Console.WriteLine()
            failwith "nuget process' output contained errors ^"

let BuildSolution buildTool solutionFileName binaryConfig extraOptions =

    let configOption = sprintf "/p:Configuration=%s" (binaryConfig.ToString())
    let configOptions =
        match buildConfigContents |> Map.tryFind "DefineConstants" with
        | Some constants -> sprintf "%s;DefineConstants=%s" configOption constants
        | None   -> configOption
    let buildProcess = Process.Execute (sprintf "%s %s %s %s"
                                                buildTool
                                                solutionFileName
                                                configOptions
                                                extraOptions,
                                        true, false)
    if (buildProcess.ExitCode <> 0) then
        Console.Error.WriteLine (sprintf "%s build failed" buildTool)
        PrintNugetVersion() |> ignore
        Environment.Exit 1

let JustBuild binaryConfig: Frontend =
    Console.WriteLine "Compiling gwallet..."
    let buildTool = Map.tryFind "BuildTool" buildConfigContents
    if buildTool.IsNone then
        failwith "A BuildTool should have been chosen by the configure script, please report this bug"

    BuildSolution buildTool.Value DEFAULT_SOLUTION_FILE binaryConfig String.Empty

    let frontend =
        // older mono versions (which only have xbuild, not msbuild) can't compile .NET Standard assemblies
        if buildTool.Value = "msbuild" && Misc.GuessPlatform () = Misc.Platform.Linux then

            let isGtkPresent = (Process.Execute("pkg-config gtk-sharp-2.0", true, false).ExitCode = 0)

            if isGtkPresent then

                // somehow, msbuild doesn't restore the dependencies of the GTK frontend (Xamarin.Forms in particular)
                // when targetting the LINUX_SOLUTION_FILE below, so we need this workaround. TODO: report this bug
                let nugetWorkaround =
                    sprintf "mono .nuget/nuget.exe restore src/%s/%s.fsproj -SolutionDirectory ." GTK_FRONTEND GTK_FRONTEND
                Process.Execute(nugetWorkaround, true, false) |> ignore

                BuildSolution "msbuild" LINUX_SOLUTION_FILE binaryConfig "/t:Restore"
                // TODO: report as a bug the fact that /t:Restore;Build doesn't work while /t:Restore and later /t:Build does
                BuildSolution "msbuild" LINUX_SOLUTION_FILE binaryConfig "/t:Build"
                Frontend.Gtk
            else
                Frontend.Console
        else
            Frontend.Console

    Directory.CreateDirectory(launcherScriptPath.Directory.FullName) |> ignore
    let wrapperScriptWithPaths =
        wrapperScript.Replace("$TARGET_DIR", libInstallPath.FullName)
                     .Replace("$GWALLET_PROJECT", CONSOLE_FRONTEND)
    File.WriteAllText (launcherScriptPath.FullName, wrapperScriptWithPaths)
    frontend

let MakeCheckCommand (commandName: string) =
    if (Process.CommandCheck commandName).IsNone then
        Console.Error.WriteLine (sprintf "%s not found, please install it first" commandName)
        Environment.Exit 1

let GetPathToFrontend (frontend: Frontend) (binaryConfig: BinaryConfig): DirectoryInfo*FileInfo =
    let frontendProjName =
        match frontend with
        | Frontend.Console -> CONSOLE_FRONTEND
        | Frontend.Gtk -> GTK_FRONTEND
    let dir = Path.Combine ("src", frontendProjName, "bin", binaryConfig.ToString()) |> DirectoryInfo
    let mainExecFile = Path.Combine(dir.FullName, frontendProjName + ".exe") |> FileInfo
    dir,mainExecFile

let maybeTarget = GatherTarget (Util.FsxArguments(), None)
match maybeTarget with
| None ->
    Console.WriteLine "Building gwallet in DEBUG mode..."
    JustBuild BinaryConfig.Debug
        |> ignore

| Some("release") ->
    JustBuild BinaryConfig.Release
        |> ignore

| Some "nuget" ->
    Console.WriteLine "This target is for debugging purposes."

    if not (PrintNugetVersion()) then
        Console.Error.WriteLine "Nuget executable has not been downloaded yet, try `make` alone first"
        Environment.Exit 1

| Some("zip") ->
    let zipCommand = "zip"
    MakeCheckCommand zipCommand

    let version = Misc.GetCurrentVersion(rootDir).ToString()

    let release = BinaryConfig.Release
    let frontend = JustBuild release
    let binDir = "bin"
    Directory.CreateDirectory(binDir) |> ignore

    let zipNameWithoutExtension = sprintf "gwallet-%s.v.%s" (frontend.ToString().ToLower()) version
    let zipName = sprintf "%s.zip" zipNameWithoutExtension
    let pathToZip = Path.Combine(binDir, zipName)
    if (File.Exists (pathToZip)) then
        File.Delete (pathToZip)

    let pathToFolderToBeZipped = Path.Combine(binDir, zipNameWithoutExtension)
    if (Directory.Exists (pathToFolderToBeZipped)) then
        Directory.Delete (pathToFolderToBeZipped, true)

    let pathToFrontend,_ = GetPathToFrontend frontend release
    let zipRun = Process.Execute(sprintf "cp -rfvp %s %s" pathToFrontend.FullName pathToFolderToBeZipped, true, false)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "Precopy for ZIP compression failed"
        Environment.Exit 1

    let previousCurrentDir = Directory.GetCurrentDirectory()
    Directory.SetCurrentDirectory binDir
    let zipLaunch = sprintf "%s -r %s %s"
                            zipCommand zipName zipNameWithoutExtension
    let zipRun = Process.Execute(zipLaunch, true, false)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "ZIP compression failed"
        Environment.Exit 1
    Directory.SetCurrentDirectory previousCurrentDir

| Some("check") ->
    Console.WriteLine "Running tests..."
    Console.WriteLine ()

    let nunitCommand = "nunit-console"
    MakeCheckCommand nunitCommand
    let testAssembly = "GWallet.Backend.Tests"
    let testAssemblyPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "src", testAssembly, "bin",
                                        testAssembly + ".dll")
    if not (File.Exists(testAssemblyPath)) then
        failwithf "File not found: %s" testAssemblyPath
    let nunitRun = Process.Execute(sprintf "%s %s" nunitCommand testAssemblyPath,
                                   true, false)
    if (nunitRun.ExitCode <> 0) then
        Console.Error.WriteLine "Tests failed"
        Environment.Exit 1

| Some("install") ->
    Console.WriteLine "Building gwallet in RELEASE mode..."
    JustBuild BinaryConfig.Release
        |> ignore

    Console.WriteLine "Installing gwallet..."
    Console.WriteLine ()
    Directory.CreateDirectory(libInstallPath.FullName) |> ignore
    Misc.CopyDirectoryRecursively (mainBinariesPath, libInstallPath)

    let finalPrefixPathOfWrapperScript = FileInfo (Path.Combine(binInstallPath.FullName, launcherScriptPath.Name))
    if not (Directory.Exists(finalPrefixPathOfWrapperScript.Directory.FullName)) then
        Directory.CreateDirectory(finalPrefixPathOfWrapperScript.Directory.FullName) |> ignore
    File.Copy(launcherScriptPath.FullName, finalPrefixPathOfWrapperScript.FullName, true)
    if ((Process.Execute(sprintf "chmod ugo+x %s" finalPrefixPathOfWrapperScript.FullName, false, true)).ExitCode <> 0) then
        failwith "Unexpected chmod failure, please report this bug"

| Some("run") ->
    let fullPathToMono = Process.CommandCheck "mono"
    if (fullPathToMono.IsNone) then
        Console.Error.WriteLine "mono not found? install it first"
        Environment.Exit 1

    let debug = BinaryConfig.Debug
    let frontend = JustBuild debug

    let frontendDir,frontendExecutable = GetPathToFrontend frontend debug

    let proc = System.Diagnostics.Process.Start
                   (fullPathToMono.Value, frontendExecutable.FullName)
    proc.WaitForExit()

| Some "update-servers" ->
    let utxoCoinFolder = Path.Combine("src", "GWallet.Backend", "UtxoCoin")

    let btcServersUrl = "https://raw.githubusercontent.com/spesmilo/electrum/master/electrum/servers.json"
    let btcServersFile = Path.Combine(utxoCoinFolder, "btc-servers.json")
    let updateBtc = Process.Execute (sprintf "curl --fail -o %s %s" btcServersFile btcServersUrl, true, false)
    if (updateBtc.ExitCode <> 0) then
        Console.Error.WriteLine "Update failed"
        Environment.Exit 1

    let ltcServersUrl = "https://raw.githubusercontent.com/pooler/electrum-ltc/master/electrum_ltc/servers.json"
    let ltcServersFile = Path.Combine(utxoCoinFolder, "ltc-servers.json")
    let updateLtc = Process.Execute (sprintf "curl --fail -o %s %s" ltcServersFile ltcServersUrl, true, false)
    if (updateLtc.ExitCode <> 0) then
        Console.Error.WriteLine "Update failed"
        Environment.Exit 1

| Some(someOtherTarget) ->
    Console.Error.WriteLine("Unrecognized target: " + someOtherTarget)
    Environment.Exit 2