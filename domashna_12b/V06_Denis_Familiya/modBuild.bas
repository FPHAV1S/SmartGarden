Attribute VB_Name = "modBuild"
Option Compare Database
Option Explicit
' Late binding: no reference to the DAO / ACE library is needed.

' ------------------------------------------------------------------
' Project helper module.
' BuildAll        - creates the saved queries, forms and reports from
'                   the zBuild_* tables in this file and exports the
'                   two reports to PDF next to the .accdb file.
' RunActionTests  - ONLY in the *_TEST.accdb copy: runs A1-A4 with the
'                   documented parameters and writes Test_Log.
' OpenObj         - used by the buttons in F_Menu.
' FillPrice       - used by the service/price combo box in subforms.
' ------------------------------------------------------------------

Public Function OpenObj(ByVal spec As String)
    Dim kind As String, nm As String
    kind = Left$(spec, 1)
    nm = Mid$(spec, 3)
    Select Case kind
        Case "Q": DoCmd.OpenQuery nm
        Case "R": DoCmd.OpenReport nm, acViewPreview
        Case "F": DoCmd.OpenForm nm
    End Select
End Function

' Copies column 2 of the active combo box (current catalogue price)
' into UnitPrice, but only when UnitPrice is still empty, so an agreed
' price that is already stored is never overwritten.
Public Function FillPrice()
    Dim c As Control
    On Error Resume Next
    Set c = Screen.ActiveControl
    If IsNull(c.Parent!UnitPrice) Then c.Parent!UnitPrice = c.Column(2)
End Function

Private Function TableExists(ByVal nm As String) As Boolean
    Dim t As Object
    For Each t In CurrentDb.TableDefs
        If t.Name = nm Then TableExists = True: Exit Function
    Next
End Function

Private Sub DeleteIfExists(ByVal objType As AcObjectType, ByVal nm As String)
    On Error Resume Next
    DoCmd.Close objType, nm, acSaveNo
    DoCmd.DeleteObject objType, nm
    Err.Clear
End Sub

Public Sub BuildAll()
    If Not TableExists("zBuild_Queries") Then
        MsgBox "zBuild_Queries is missing - the objects are already built.", vbInformation
        Exit Sub
    End If
    BuildQueries
    BuildObjects
    SetStartupForm
    If InStr(1, CurrentProject.Name, "_TEST", vbTextCompare) = 0 Then ExportReports
    DoCmd.SetWarnings False
    DeleteIfExists acTable, "zBuild_Queries"
    DeleteIfExists acTable, "zBuild_Objects"
    DeleteIfExists acTable, "zBuild_Controls"
    DoCmd.SetWarnings True
    MsgBox "Build finished: queries, forms and reports are created.", vbInformation
End Sub

Private Sub BuildQueries()
    Dim db As Object, rs As Object
    Set db = CurrentDb
    Set rs = db.OpenRecordset("SELECT * FROM zBuild_Queries ORDER BY Seq", 4)
    Do Until rs.EOF
        On Error Resume Next
        db.QueryDefs.Delete rs!QName
        Err.Clear
        On Error GoTo 0
        db.CreateQueryDef rs!QName, rs!SQLText
        rs.MoveNext
    Loop
    rs.Close
    db.QueryDefs.Refresh
End Sub

Private Sub BuildObjects()
    Dim rs As Object
    Set rs = CurrentDb.OpenRecordset("SELECT * FROM zBuild_Objects ORDER BY Seq", 4)
    Do Until rs.EOF
        If rs!ObjType = "FORM" Then
            BuildForm rs
        Else
            BuildReport rs
        End If
        rs.MoveNext
    Loop
    rs.Close
End Sub

Private Function HasSection(ByVal nm As String, ByVal s1 As Integer, ByVal s2 As Integer) As Boolean
    HasSection = DCount("*", "zBuild_Controls", "ObjName='" & nm & "' AND (Section=" & s1 & " OR Section=" & s2 & ")") > 0
End Function

Private Sub BuildForm(rsO As Object)
    Dim frm As Form, tmp As String, nm As String
    nm = rsO!ObjName
    DeleteIfExists acForm, nm
    Set frm = CreateForm()
    tmp = frm.Name
    frm.RecordSource = Nz(rsO!RecordSource, "")
    frm.Caption = Nz(rsO!Caption, nm)
    frm.DefaultView = rsO!DefaultView
    frm.Width = rsO!Wd
    If Nz(rsO!RecordSource, "") = "" Then
        frm.RecordSelectors = False
        frm.NavigationButtons = False
        frm.DividingLines = False
    End If
    If HasSection(nm, acHeader, acFooter) Then
        On Error Resume Next
        DoCmd.RunCommand acCmdFormHdrFtr
        Err.Clear
        On Error GoTo 0
    End If
    AddControls frm, nm, False
    SetSectionHeights frm, nm
    DoCmd.Close acForm, tmp, acSaveYes
    DoCmd.Rename nm, acForm, tmp
End Sub

Private Sub BuildReport(rsO As Object)
    Dim rpt As Report, tmp As String, nm As String, parts() As String, i As Integer, lvl As Long, p() As String
    nm = rsO!ObjName
    DeleteIfExists acReport, nm
    Set rpt = CreateReport()
    tmp = rpt.Name
    rpt.RecordSource = Nz(rsO!RecordSource, "")
    rpt.Caption = Nz(rsO!Caption, nm)
    rpt.Width = rsO!Wd
    If HasSection(nm, acHeader, acFooter) Then
        On Error Resume Next
        DoCmd.RunCommand acCmdReportHdrFtr
        Err.Clear
        On Error GoTo 0
    End If
    If Not SectionExists(rpt, acPageHeader) Then
        On Error Resume Next
        DoCmd.RunCommand acCmdPageHdrFtr
        Err.Clear
        On Error GoTo 0
    End If
    ' Sorting: "Field:DESC,Field2:ASC"
    If Nz(rsO!SortSpec, "") <> "" Then
        parts = Split(rsO!SortSpec, ",")
        For i = 0 To UBound(parts)
            p = Split(parts(i), ":")
            lvl = CreateGroupLevel(rpt.Name, p(0), False, False)
            rpt.GroupLevel(lvl).SortOrder = (UCase$(p(1)) = "DESC")
        Next
    End If
    AddControls rpt, nm, True
    SetSectionHeights rpt, nm
    DoCmd.Close acReport, tmp, acSaveYes
    DoCmd.Rename nm, acReport, tmp
End Sub

Private Function SectionExists(obj As Object, ByVal s As Integer) As Boolean
    Dim h As Long
    On Error GoTo NoSec
    h = obj.Section(s).Height
    SectionExists = True
    Exit Function
NoSec:
    SectionExists = False
End Function

Private Sub SetSectionHeights(obj As Object, ByVal nm As String)
    Dim s As Integer, mx As Variant
    On Error Resume Next
    For s = 0 To 4
        If SectionExists(obj, s) Then
            mx = DMax("Tp+Ht", "zBuild_Controls", "ObjName='" & nm & "' AND Section=" & s)
            If IsNull(mx) Then
                obj.Section(s).Height = 0
            Else
                obj.Section(s).Height = mx + 60
            End If
        End If
    Next
    Err.Clear
End Sub

Private Sub AddControls(obj As Object, ByVal nm As String, ByVal isReport As Boolean)
    Dim rs As Object, ctl As Control, t As Integer, extra() As String, i As Integer, kv() As String
    Set rs = CurrentDb.OpenRecordset("SELECT * FROM zBuild_Controls WHERE ObjName='" & nm & "' ORDER BY Seq", 4)
    Do Until rs.EOF
        Select Case rs!CtlType
            Case "LABEL": t = acLabel
            Case "TEXT": t = acTextBox
            Case "COMBO": t = acComboBox
            Case "CHECK": t = acCheckBox
            Case "SUBFORM": t = acSubform
            Case "BUTTON": t = acCommandButton
            Case "LINE": t = acLine
        End Select
        If isReport Then
            Set ctl = CreateReportControl(obj.Name, t, rs!Section, , , rs!Lft, rs!Tp, rs!Wd, rs!Ht)
        Else
            Set ctl = CreateControl(obj.Name, t, rs!Section, , , rs!Lft, rs!Tp, rs!Wd, rs!Ht)
        End If
        ctl.Name = rs!CtlName
        Select Case rs!CtlType
            Case "LABEL"
                ctl.Caption = Nz(rs!Caption, "")
            Case "TEXT", "CHECK", "COMBO"
                ctl.ControlSource = Nz(rs!Source, "")
                If rs!CtlType = "COMBO" Then ctl.RowSource = Nz(rs!RowSource, "")
            Case "SUBFORM"
                ctl.SourceObject = "Form." & rs!Source
            Case "BUTTON"
                ctl.Caption = Nz(rs!Caption, "")
                ctl.OnClick = "=OpenObj(""" & rs!Source & """)"
        End Select
        ' Not every control type has every property (e.g. a check box has
        ' no FontSize), so these optional settings never stop the build.
        On Error Resume Next
        If Nz(rs!FontSize, 0) > 0 Then ctl.FontSize = rs!FontSize
        If Nz(rs!Bold, False) Then ctl.FontBold = True
        If Nz(rs!Extra, "") <> "" Then
            extra = Split(rs!Extra, "|")
            For i = 0 To UBound(extra)
                kv = Split(extra(i), "=", 2)
                If IsNumeric(kv(1)) Then
                    ctl.Properties(kv(0)) = CLng(kv(1))
                Else
                    ctl.Properties(kv(0)) = kv(1)
                End If
                If Err.Number <> 0 Then Debug.Print "Skipped " & nm & "." & ctl.Name & "." & kv(0) & ": " & Err.Description: Err.Clear
            Next
        End If
        Err.Clear
        On Error GoTo 0
        rs.MoveNext
    Loop
    rs.Close
End Sub

Private Sub SetStartupForm()
    Dim db As Object
    Set db = CurrentDb
    On Error Resume Next
    db.Properties("StartUpForm") = "F_Menu"
    If Err.Number <> 0 Then
        Err.Clear
        db.Properties.Append db.CreateProperty("StartUpForm", 10, "F_Menu")
    End If
    Err.Clear
End Sub

Public Sub ExportReports()
    Dim base As String
    base = CurrentProject.Path & "\" & Replace(CurrentProject.Name, ".accdb", "", , , vbTextCompare)
    DoCmd.OutputTo acOutputReport, "R_Summary", acFormatPDF, base & "_R_Summary.pdf"
    DoCmd.OutputTo acOutputReport, "R_Attention", acFormatPDF, base & "_R_Attention.pdf"
End Sub

' ------------------------------------------------------------------
' Runs A1-A4 (and the repeated A2 check) with the parameters stored in
' zBuild_Tests. Every step is written to the table Test_Log with the
' number of affected records and the row count before/after.
' ------------------------------------------------------------------
Public Sub RunActionTests()
    Dim db As Object, rs As Object, qd As Object
    Dim vals() As String, i As Integer, before As Long, after As Long, affected As Long
    If InStr(1, CurrentProject.Name, "_TEST", vbTextCompare) = 0 Then
        MsgBox "RunActionTests works only in the *_TEST.accdb copy.", vbExclamation
        Exit Sub
    End If
    If Not TableExists("zBuild_Tests") Then
        MsgBox "zBuild_Tests is missing - the tests were already run. Start again from a fresh copy.", vbExclamation
        Exit Sub
    End If
    Set db = CurrentDb
    If Not TableExists("Test_Log") Then
        db.Execute "CREATE TABLE Test_Log (Step LONG, QueryName TEXT(64), Params TEXT(255), CountedTable TEXT(64), RowsBefore LONG, RecordsAffected LONG, RowsAfter LONG, RunAt DATETIME)"
    End If
    Set rs = db.OpenRecordset("SELECT * FROM zBuild_Tests ORDER BY Seq", 4)
    Do Until rs.EOF
        If rs!QName = "A4_MakeSnapshotQ5" Then
            DoCmd.SetWarnings False
            DeleteIfExists acTable, "Snapshot_Q5"
            DoCmd.SetWarnings True
        End If
        before = CountRows(rs!CountTable)
        Set qd = db.QueryDefs(rs!QName)
        If Nz(rs!ParamValues, "") <> "" Then
            vals = Split(rs!ParamValues, "|")
            For i = 0 To UBound(vals)
                If vals(i) Like "####-##-##" Then
                    qd.Parameters(i) = DateSerial(CInt(Left$(vals(i), 4)), CInt(Mid$(vals(i), 6, 2)), CInt(Right$(vals(i), 2)))
                Else
                    qd.Parameters(i) = CLng(vals(i))
                End If
            Next
        End If
        qd.Execute 128
        affected = qd.RecordsAffected
        after = CountRows(rs!CountTable)
        db.Execute "INSERT INTO Test_Log (Step, QueryName, Params, CountedTable, RowsBefore, RecordsAffected, RowsAfter, RunAt) VALUES (" & _
            rs!Seq & ", '" & rs!QName & "', '" & Nz(rs!ParamValues, "") & "', '" & rs!CountTable & "', " & before & ", " & affected & ", " & after & ", Now())", 128
        rs.MoveNext
    Loop
    rs.Close
    DoCmd.SetWarnings False
    DeleteIfExists acTable, "zBuild_Tests"
    DoCmd.SetWarnings True
    MsgBox "A1-A4 finished. See the table Test_Log.", vbInformation
    DoCmd.OpenTable "Test_Log"
End Sub

Private Function CountRows(ByVal tbl As String) As Long
    On Error Resume Next
    CountRows = -1
    CountRows = DCount("*", tbl)
End Function
