Public Module GlobalState
    Public ReadOnly DbLock As New Object()
    Public CurrentTarget As ExecutionTarget = ExecutionTarget.LocalWindows
    Public MacHost As String = ""
    Public MacUser As String = ""
    Public MacPort As Integer = 5000
End Module

Public Enum ExecutionTarget
    LocalWindows
    RemoteMac
End Enum