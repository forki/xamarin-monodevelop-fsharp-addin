﻿namespace MonoDevelop.FSharp

open System
open ExtCore.Control
open Gtk
open MonoDevelop.Components.Docking
open MonoDevelop.Components
open MonoDevelop.Core
open MonoDevelop.DesignerSupport
open MonoDevelop.Ide.Editor.Extension
open MonoDevelop.Ide.Editor
open MonoDevelop.Ide.Gui.Components
open MonoDevelop.Ide.Gui.Content
open MonoDevelop.Ide.TypeSystem
open MonoDevelop.Ide
open MonoDevelop.Projects
open Microsoft.FSharp.Compiler.SourceCodeServices

type FSharpOutlineTextEditorExtension() as x =
    inherit TextEditorExtension()
    let mutable treeView : PadTreeView option = None
    let mutable refreshingOutline : bool = false
    let mutable treeViewRealized : bool = false
    let mutable timerId : uint32 = 0u
    let mutable handler : IDisposable = null

    let refillTree() =
        match treeView with
        | Some(treeView) ->
            if treeViewRealized then
                let ast = maybe { let! context = x.DocumentContext |> Option.ofNull
                                  let! parsedDocument = context.ParsedDocument |> Option.ofNull
                                  let! ast = parsedDocument.Ast |> Option.tryCast<ParseAndCheckResults>
                                  return ast }

                DispatchService.AssertGuiThread()
                Gdk.Threads.Enter()
                refreshingOutline <- false

                if treeView.IsRealized then
                    ast |> Option.iter (fun ast ->
                        let treeStore = treeView.Model :?> TreeStore
                        treeStore.Clear()
                        let toplevel = ast.GetNavigationItems()
                                       |> Array.sortBy(fun xs -> xs.Declaration.Range.StartLine)

                        for item in toplevel do
                            let iter = treeStore.AppendValues(item.Declaration)
                            let children = item.Nested
                                           |> Array.sortBy(fun xs -> xs.Range.StartLine)

                            for nested in children do
                                treeStore.AppendValues(iter, [| nested |]) |> ignore

                        treeView.ExpandAll())
                    Gdk.Threads.Leave()
                    timerId <- 0u
        | None -> ()

        refreshingOutline <- false
        false

    member private x.updateDocumentOutline _ =
      if not refreshingOutline then
        refreshingOutline <- true
        timerId <- GLib.Timeout.Add (1000u, (fun _ -> refillTree()))

    override x.Initialize() =
        base.Initialize()
        handler <- x.DocumentContext.DocumentParsed.Subscribe(fun o e -> x.updateDocumentOutline())
        ()

    override x.Dispose() =
        handler.Dispose()
        if timerId > 0u then
            GLib.Source.Remove timerId |> ignore
        timerId <- 0u
        base.Dispose()

    override x.IsValidInContext context =
        LanguageBindingService.GetBindingPerFileName (context.Name) <> null;

    interface IOutlinedDocument with
        member x.GetOutlineWidget() =
            match treeView with
            | Some(treeView) -> treeView :> Widget
            | None ->
                let treeStore = new TreeStore(typedefof<obj>)
                let padTreeView = new PadTreeView(treeStore, HeadersVisible = true)

                let setCellIcon _column (cellRenderer : CellRenderer) (treeModel : TreeModel) (iter : TreeIter) =
                    let pixRenderer = cellRenderer :?> CellRendererImage
                    let item = treeModel.GetValue(iter, 0) :?> FSharpNavigationDeclarationItem
                    pixRenderer.Image <- ImageService.GetIcon(ServiceUtils.getIcon item, Gtk.IconSize.Menu)

                let setCellText _column (cellRenderer : CellRenderer) (treeModel : TreeModel) (iter : TreeIter) =
                    let renderer = cellRenderer :?> CellRendererText
                    let item = treeModel.GetValue(iter, 0) :?> FSharpNavigationDeclarationItem
                    renderer.Text <- item.Name
                let jumpToDeclaration focus =
                    let iter : TreeIter ref = ref Unchecked.defaultof<_>
                    if padTreeView.Selection.GetSelected(iter) then
                        let node = padTreeView.Model.GetValue(!iter, 0) :?> FSharpNavigationDeclarationItem
                        let (scol,sline) = node.Range.StartColumn, node.Range.StartLine
                        x.Editor.SetCaretLocation(max 1 sline, max 1 scol, true)

                    if focus then
                        x.Editor.GrabFocus()

                treeView <- Some padTreeView

                let pixRenderer = new CellRendererImage(Xpad = 0u, Ypad = 0u)
                padTreeView.TextRenderer.Xpad <- 0u
                padTreeView.TextRenderer.Ypad <- 0u

                let treeCol = new TreeViewColumn()
                treeCol.PackStart(pixRenderer, false)
                treeCol.SetCellDataFunc(pixRenderer, new TreeCellDataFunc(setCellIcon))
                treeCol.PackStart(padTreeView.TextRenderer, true)
                treeCol.SetCellDataFunc(padTreeView.TextRenderer, new TreeCellDataFunc(setCellText))

                padTreeView.AppendColumn treeCol |> ignore
                padTreeView.Realized.Add(fun _ -> treeViewRealized <- true; refillTree |> ignore)
                padTreeView.Selection.Changed.Subscribe(fun _ -> jumpToDeclaration false) |> ignore
                padTreeView.RowActivated.Subscribe(fun _ -> jumpToDeclaration true) |> ignore

                let sw = new CompactScrolledWindow()
                sw.Add padTreeView
                sw.ShowAll()
                sw :> Widget

        member x.GetToolbarWidgets() = List.empty<Widget> :> _

        member x.ReleaseOutlineWidget() =
            treeView |> Option.iter(fun tv -> Option.tryCast<ScrolledWindow>(tv.Parent) 
                                              |> Option.iter (fun sw -> sw.Destroy())
                                              let treeStore = tv.Model :?> TreeStore
                                              if treeStore <> null then
                                                  treeStore.Dispose()

                                              treeView <- None)
