#Requires AutoHotkey v2.0
#Include Framework\Benchmarker.ahk

#Include ..\..\AhkWin32projection\Windows\Win32\Foundation\DECIMAL.ahk
#Include ..\..\AhkWin32projection\Windows\Win32\Graphics\Gdi\DISPLAY_DEVICEW.ahk
#Include ..\..\AhkWin32projection\Windows\Win32\UI\ColorSystem\ENUMTYPEW.ahk
#Include ..\..\AhkWin32projection\Windows\Win32\System\LibraryLoader\Apis.ahk

#Include ..\..\AhkWin32projection\Windows\Win32\Security\Cryptography\Apis.ahk

;Number of runs to do
RUNS := 10000

benchmark := Benchmarker("Reports\Benchmarks")

benchmark.RptHeader("Basic Tests")
benchmark.Run("Allocate & Deallocate DECIMAL (16 bytes)", (*) => DECIMAL(), RUNS)

testDec := DECIMAL()
num := 1
benchmark.Run("Set Integer", (*) => testDec.Lo32 := num, RUNS)
benchmark.Run("Get Integer", (*) => tmp := testDec.Lo32, RUNS)

testDec.Lo32 := 0
benchmark.Run("Postincrement Struct Integer Property", (*) => testDec.Lo32++, RUNS)
testDec.Lo32 := 0
benchmark.Run("Add-Assign Operator (+=)", (*) => testDec.Lo32 += 100, RUNS)

benchmark.Run("Allocate & Deallocate DISPLAY_DEVICEW (840 bytes)", (*) => DISPLAY_DEVICEW(), RUNS)
benchmark.Run("Allocate & Deallocate ENUMTYPEW (96 bytes)", (*) => ENUMTYPEW(), RUNS)

benchmark.RptHeader("String Tests")

testDD := DISPLAY_DEVICEW()
benchmark.Run("Set String Value (`"test test`")", (*) => testDD.DeviceName := "test test", RUNS)
benchmark.Run("Get String Value", (*) => tmp := testDD.DeviceName, RUNS)

testDD.DeviceName := "test"
benchmark.RunWithReset("String Append (.= `"test`")", (*) => testDD.DeviceName .= "test", (*) => testDD.DeviceName := "test", RUNS)

benchmark.RptHeader("DllCall Tests")
rand := Buffer(4)
benchmark.Run("Wrapper function, dll not loaded (BCryptPrimitives\ProcessPrng)", Cryptography.ProcessPrng.Bind(Cryptography, rand, 4), RUNS)
benchmark.Run("Direct DllCall, dll not loaded (BCryptPrimitives\ProcessPrng)", (*) => DllCall("BCryptPrimitives.dll\ProcessPrng", "ptr*", &tmp := 0, "ptr", 4, "int"), RUNS)

;Load library
hLibCrypt := LibraryLoader.LoadLibraryW("BCryptPrimitives")

benchmark.Run("Wrapper function, dll loaded (BCryptPrimitives\ProcessPrng)", Cryptography.ProcessPrng.Bind(Cryptography, rand, 4), RUNS)
benchmark.Run("Direct DllCall, dll loaded (BCryptPrimitives\ProcessPrng)", (*) => DllCall("BCryptPrimitives.dll\ProcessPrng", "ptr*", &tmp := 0, "ptr", 4, "int"), RUNS)

; FreeLibrary isn't in LibraryLoader? Doesn't show up in ILSpy either. FreeLibraryAndExitThread does appear though...
DllCall("FreeLibrary", "ptr", hLibCrypt)