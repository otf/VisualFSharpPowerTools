﻿namespace FSharpVSPowerTools.ProjectSystem

open System
open System.ComponentModel.Composition

open FSharpVSPowerTools
open FSharp.CompilerBinding
open Microsoft.VisualStudio.Text
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.VisualStudio.Shell

[<Export>]
type VSLanguageService [<ImportingConstructor>] ([<Import(typeof<SVsServiceProvider>)>] serviceProvider: IServiceProvider) =
    // TODO: we should reparse the stale document and cache it
    let instance = FSharp.CompilerBinding.LanguageService(fun _ -> ())
    let invalidateProject (projectItem: EnvDTE.ProjectItem) =
        let project = projectItem.ContainingProject
        if box project <> null && isFSharpProject project then
            let p = ProjectProvider.createForProject project
            debug "[Language Service] InteractiveChecker.InvalidateConfiguration for %s" p.ProjectFileName
            let opts = instance.GetCheckerOptions (null, p.ProjectFileName, null, p.SourceFiles, 
                                                   p.CompilerOptions, p.TargetFramework)
            instance.InvalidateConfiguration(opts)

    let dte = serviceProvider.GetService<EnvDTE.DTE, Interop.SDTE>()
    let events = dte.Events :?> EnvDTE80.Events2
    let projectItemsEvents = events.ProjectItemsEvents
    do projectItemsEvents.add_ItemAdded(fun p -> invalidateProject p)
    do projectItemsEvents.add_ItemRemoved(fun p -> invalidateProject p)
    do projectItemsEvents.add_ItemRenamed(fun p _ -> invalidateProject p)

    member x.TryGetLocation (symbol: FSharpSymbol) =
        Option.orElse symbol.ImplementationLocation symbol.DeclarationLocation

    member x.GetSymbol(point: SnapshotPoint, projectProvider : IProjectProvider) =
        let source = point.Snapshot.GetText()
        let line = point.Snapshot.GetLineNumberFromPosition point.Position
        let col = point.Position - point.GetContainingLine().Start.Position
        let lineStr = point.GetContainingLine().GetText()                
        let args = projectProvider.CompilerOptions                
        SymbolParser.getSymbol source line col lineStr args
        |> Option.map (fun symbol -> point.FromRange symbol.Range, symbol)

    member x.ProcessNavigableItemsInProject(openDocuments, (projectProvider: IProjectProvider), processNavigableItems, ct) =
        instance.ProcessParseTrees(
            projectProvider.ProjectFileName, 
            openDocuments, 
            projectProvider.SourceFiles, 
            projectProvider.CompilerOptions, 
            projectProvider.TargetFramework, 
            (Navigation.NavigableItemsCollector.collect >> processNavigableItems), 
            ct)

    member x.FindUsages (word: SnapshotSpan, currentFile: string, projectProvider: IProjectProvider) =
        async {
            try 
                let (_, _, endLine, endCol) = word.ToRange()
                let projectFileName = projectProvider.ProjectFileName
                let source = word.Snapshot.GetText()
                let currentLine = word.Start.GetContainingLine().GetText()
                let framework = projectProvider.TargetFramework
                let args = projectProvider.CompilerOptions
                let sourceFiles = 
                    match projectProvider.SourceFiles with
                    // If there is no source file, use currentFile as an independent script
                    | [||] -> [| currentFile |] 
                    | files -> files
            
                debug "[Language Service] Get symbol references for '%s' at line %d col %d on %A framework and '%s' arguments" 
                      (word.GetText()) endLine endCol framework (String.concat " " args)
            
                return! 
                    instance.GetUsesOfSymbolInProjectAtLocationInFile (projectFileName, currentFile, source, sourceFiles, 
                                                                       endLine, endCol, currentLine, args, framework)
            with e ->
                debug "[Language Service] %O exception occurs while updating." e
                return None }

    member x.FindUsagesInFile (word: SnapshotSpan, sym: Symbol, currentFile: string, projectProvider: IProjectProvider, stale) =
        async {
            try 
                let (_, _, endLine, endCol) = word.ToRange()
                let framework = projectProvider.TargetFramework
                let args = projectProvider.CompilerOptions
            
                debug "[Language Service] Get symbol references for '%s' at line %d col %d on %A framework and '%s' arguments" 
                      (word.GetText()) endLine endCol framework (String.concat " " args)
            
                let! res = x.GetFSharpSymbol (word, sym, currentFile, projectProvider, stale)
                return res |> Option.map (fun (_, checkResults) -> x.FindUsagesInFile (word, sym, checkResults))
            with e ->
                debug "[Language Service] %O exception occurs while updating." e
                return None }

    member x.FindUsagesInFile (word: SnapshotSpan, sym: Symbol, fileScopedCheckResults: ParseAndCheckResults) =
        try 
            let (_, _, endLine, _) = word.ToRange()
            let currentLine = word.Start.GetContainingLine().GetText()
            
            debug "[Language Service] Get symbol references for '%s' at line %d col %d" (word.GetText()) endLine sym.RightColumn
            fileScopedCheckResults.GetUsesOfSymbolInFileAtLocation (endLine, sym.RightColumn, currentLine, sym.Text)
        with e ->
            debug "[Language Service] %O exception occurs while updating." e
            None

    member x.GetFSharpSymbol (word: SnapshotSpan, symbol: Symbol, currentFile: string, projectProvider: IProjectProvider, stale) = 
        async {
            let (_, _, endLine, _) = word.ToRange()
            let projectFileName = projectProvider.ProjectFileName
            let source = word.Snapshot.GetText()
            let currentLine = word.Start.GetContainingLine().GetText()
            let framework = projectProvider.TargetFramework
            let args = projectProvider.CompilerOptions
            let sourceFiles = 
                match projectProvider.SourceFiles with
                // If there is no source file, use currentFile as an independent script
                | [||] -> [| currentFile |] 
                | files -> files
            let! results = instance.ParseAndCheckFileInProject(projectFileName, currentFile, source, sourceFiles, args, framework, stale)
            let symbol = results.GetSymbolAtLocation (endLine+1, symbol.RightColumn, currentLine, [symbol.Text])
            return symbol |> Option.map (fun s -> s, results)
        }

    member x.Checker = instance.Checker
