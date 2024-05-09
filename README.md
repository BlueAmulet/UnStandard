# UnStandard
UnStandard is a tool based on [dnlib](https://github.com/0xd4d/dnlib) to convert .NET dlls targeting .NET Standard to .NET Framework 4.  
Primarily created to continue to (ab)use Unity 2022+ dlls in projects targeting both .NET Framework and Mono.

## Usage
`UnStandard.exe --output=OutputDirectory InputFile.dll InputDirectory`

## Notes
.NET Framework does not contain all the classes included in .NET Standard 2.1, and backports are not available for all of them.  
The following libraries are used to fill in some of the missing classes in .NET Framework:
```
Microsoft.Bcl.AsyncInterfaces v8.0.0
Microsoft.Bcl.HashCode v1.1.1
Microsoft.Bcl.Numerics v8.0.0
Microsoft.CodeAnalysis v4.9.2
System.Buffers v4.5.1
System.Memory v4.5.5
System.Numerics.Vectors v4.5.0
System.Threading.Tasks.Extensions v4.5.4
```
Additionally, the Standard to Framework class mapping was generated against .NET Framework 4.7.2, and resulting dlls may not work on earlier versions of Framework.

Unity has a lot of methods marked internalcall that will crash when used outside of Unity. UnStandard automatically strips and fills in these methods with a dummy body. For internalcall methods that are getters and setters, UnStandard automatically adds in a new backing field and implements the methods respectively. This behaviour can be disabled with `--stripinternal=false`

## Known issues
The generated method bodies for internalcall methods may not be fully accurate. Notably, out byref parameters do not get initialized.  
Some Unity classes have Equals and GetHashCode methods that are based on an IntPtr. With internalcall methods dummied out these pointers will always be zero and will cause incorrect comparisons.
