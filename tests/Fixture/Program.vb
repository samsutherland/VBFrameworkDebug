Module Program
    Sub Main(args As String())
        Dim answer As Integer = 41
        Dim environment As String = "Test"
        Dim sample As New Sample With {.Count = 7}
        Dim numbers As Integer() = {12, 24}
        answer = Increment(answer) ' CALL
        Console.WriteLine(answer) ' PRINT
    End Sub

    Function Increment(value As Integer) As Integer
        Dim result As Integer = value + 1 ' HELPER
        Return result
    End Function
End Module

Class Sample
    Public Count As Integer
End Class
