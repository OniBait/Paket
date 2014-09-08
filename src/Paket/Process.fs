﻿/// Contains methods for the install and update process.
module Paket.Process

open System.IO

/// Downloads and extracts all package.
let ExtractPackages(force, packages : Package seq) = 
    packages |> Seq.map (fun package -> 
                    let version = 
                        match package.VersionRange with
                        | Specific v -> v
                        | v -> failwithf "Version error in lockfile for %s %A" package.Name v
                    match package.Source with
                    | Nuget source -> 
                        async { let! packageFile = Nuget.DownloadPackage
                                                       (source, package.Name, package.ResolverStrategy, version.ToString(), force)
                                let! folder = Nuget.ExtractPackage(packageFile, package.Name, version.ToString(), force) 
                                return package,Nuget.GetLibraries folder})

let findLockfile packageFile =
    let fi = FileInfo(packageFile)
    FileInfo(Path.Combine(fi.Directory.FullName, fi.Name + ".lock"))

let extractReferencesFromListFile projectFile =
    let fi = FileInfo(projectFile)
    let packageFile = FileInfo(Path.Combine(fi.Directory.FullName, "References.list"))
    if packageFile.Exists then File.ReadAllLines packageFile.FullName else [||]
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> System.String.IsNullOrWhiteSpace s |> not)

let private findAllProjects(folder) = DirectoryInfo(folder).EnumerateFiles("*.*proj", SearchOption.AllDirectories)

/// Installs the given packageFile.
let Install(regenerate, force, packageFile) = 
    let lockfile = findLockfile packageFile
     
    if regenerate || (not lockfile.Exists) then 
        LockFile.Update(force, packageFile, lockfile.FullName)

    let extracted = 
        ExtractPackages(force, File.ReadAllLines lockfile.FullName |> LockFile.Parse)
        |> Async.Parallel
        |> Async.RunSynchronously
    for proj in findAllProjects(".") do
        let directPackages = extractReferencesFromListFile proj.FullName
        let project = ProjectFile.Load proj.FullName

        let usedPackages = new System.Collections.Generic.HashSet<_>()

        let allPackages =
            extracted
            |> Array.map (fun (p,_) -> p.Name,p)
            |> Map.ofArray

        let rec addPackage name =
            match allPackages |> Map.tryFind name with
            | Some package ->
                if usedPackages.Add name then
                    for d in package.DirectDependencies do
                        addPackage d
            | None -> ()

        directPackages
        |> Array.iter addPackage
        
        project.UpdateReferences(extracted,usedPackages)


/// Finds all outdated packages.
let FindOutdated(packageFile) = 
    let lockfile = findLockfile packageFile
    
    let newPackages = LockFile.Create(true,packageFile)
    let installed = if lockfile.Exists then LockFile.Parse(File.ReadAllLines lockfile.FullName) else []

    [for p in installed do
        match newPackages.ResolvedVersionMap.[p.Name] with
        | Resolved newVersion -> 
            if p.VersionRange <> newVersion.Referenced.VersionRange then 
                match newVersion.Referenced.VersionRange with
                | Specific v2 -> 
                    match p.VersionRange with
                    | Specific v1 -> yield p.Name,v1,v2

        | Conflict(_) -> failwith "version conflict handling not implemented" ]

/// Prints all outdated packages.
let ListOutdated(packageFile) = 
    let allOutdated = FindOutdated packageFile
    if allOutdated = [] then
        tracefn "No outdated packages found."
    else
        tracefn "Outdated packages found:"
        for name,oldVersion,newVersion in allOutdated do
            tracefn "  * %s %s -> %s" name (oldVersion.ToString()) (newVersion.ToString())