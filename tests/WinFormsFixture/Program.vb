Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms

Module Program
    <STAThread>
    Sub Main(args As String())
        If args.Length > 1 AndAlso args(0) = "--heartbeat" Then
            Using window As New Form(), ticker As New System.Windows.Forms.Timer()
                Dim pulse As Integer
                ticker.Interval = 25
                AddHandler ticker.Tick, Sub(sender, eventArgs)
                                            IO.File.WriteAllText(args(1), pulse.ToString())
                                            pulse += 1
                                        End Sub
                ticker.Start()
                Application.Run(window)
            End Using
            Return
        End If
        If args.Length > 0 AndAlso args(0) = "--wait" Then
            Application.Run(New Form())
            Return
        End If
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)
        AddHandler Application.ThreadException, Sub(sender, eventArgs) Application.Exit()
        Application.Run(New FixtureForm("Test"))
    End Sub
End Module

Class FixtureForm
    Inherits Form

    Public Environment As String
    Public Count As Integer
    Private ReadOnly trigger As New Button()

    Public Sub New(environment As String)
        Me.Environment = environment ' FORMCTOR
        Controls.Add(trigger)
        AddHandler trigger.Click, AddressOf Clicked
        AddHandler Shown, Sub(sender, args) BeginInvoke(New Action(AddressOf trigger.PerformClick))
    End Sub

    Private Async Sub Clicked(sender As Object, args As EventArgs)
        Dim saved As String = Environment
        Count = 1 ' BEFOREAWAIT
        Await Task.Run(Sub()
                           Thread.Sleep(20) ' WORKER
                       End Sub)
        Count = 2 ' AFTERAWAIT
        If saved <> "Test" Then Throw New InvalidOperationException("Lost async local")
        Throw New InvalidOperationException("WinForms caught exception") ' FORMTHROW
    End Sub
End Class
