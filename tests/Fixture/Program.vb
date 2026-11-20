Module Program
    Sub Main(args As String())
        If args.Length > 0 AndAlso args(0) = "--multi" Then
            Console.WriteLine(1) : Console.WriteLine(2) ' MULTI
            Return
        End If
        If args.Length > 0 AndAlso args(0) = "--domain" Then
            For pass As Integer = 1 To 2
                Dim domain As AppDomain = AppDomain.CreateDomain("fixture" & pass)
                domain.ExecuteAssembly(GetType(Program).Assembly.Location, {"--count"})
                AppDomain.Unload(domain)
            Next
            Return
        End If
        If args.Length > 0 AndAlso args(0) = "--nested" Then
            Throw New InvalidOperationException("outer failure", New ArgumentException("inner failure"))
        End If
        If args.Length > 0 AndAlso args(0) = "--count" Then
            For iteration As Integer = 1 To 5
                Console.WriteLine(iteration) ' LOOP
            Next
            Return
        End If
        If args.Length > 1 AndAlso args(0) = "--heartbeat" Then
            Dim count As Integer = 0
            Do
                System.IO.File.WriteAllText(args(1), count.ToString())
                count += 1
                System.Threading.Thread.Sleep(25)
            Loop
        End If
        If args.Length > 0 AndAlso args(0) = "--caught" Then
            Try
                Throw New InvalidOperationException("fixture failure")
            Catch ex As InvalidOperationException
                Console.WriteLine(ex.Message)
            End Try
            Return
        End If
        If args.Length > 0 AndAlso args(0) = "--twice" Then
            For pass As Integer = 1 To 2
                Try
                    Throw New InvalidOperationException("caught twice")
                Catch ex As InvalidOperationException
                    Console.WriteLine(ex.Message)
                End Try
            Next
            Return
        End If
        If args.Length > 0 AndAlso args(0) = "--unhandled" Then
            Throw New InvalidOperationException("unhandled fixture failure")
        End If
        If args.Length > 0 AndAlso args(0) = "--wait" Then
            Do
                System.Threading.Thread.Sleep(10)
            Loop
        End If
        Dim answer As Integer = 41
        ' BLANK
        Dim environment As String = "Test"
        Dim sample As New Sample("Test") With {.Count = 7}
        Dim numbers As Integer() = {12, 24}
        Dim matrix As Array = Array.CreateInstance(GetType(Integer), {2, 3}, {-1, 2})
        matrix.SetValue(99, -1, 3)
        Dim boxed As Object = 123
        Dim empty As Sample = Nothing
        Dim price As Decimal = 12.5D
        Dim status As DayOfWeek = DayOfWeek.Friday
        Dim referenceValue As Integer = 9
        Update(referenceValue)
        answer = Increment(answer) ' CALL
        Console.WriteLine(answer) ' PRINT
    End Sub

    Function Increment(value As Integer) As Integer
        Dim result As Integer = value + 1 ' HELPER
        Return result
    End Function

    Sub Update(ByRef number As Integer)
        number += 1 ' BYREF
    End Sub
End Module

Class Sample
    Inherits BaseSample
    Public Count As Integer
    Public GetterCalls As Integer
    Public Sub New(environment As String)
        Count = environment.Length ' CTOR
    End Sub
    Public ReadOnly Property Dangerous As Integer
        Get
            GetterCalls += 1
            Return -1
        End Get
    End Property
End Class

Class BaseSample
    Public BaseCount As Integer = 9
End Class
