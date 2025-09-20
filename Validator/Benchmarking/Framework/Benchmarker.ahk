#Requires AutoHotkey v2.0
#Include Stopwatch.ahk

/**
 * Runs benchmarks and emits a nicely formatted output file
 */
class Benchmarker extends Object {

    /**
     * The output file
     * @type {File} 
     */
    outputFile := unset

    commit := ""
    shortcommit := ""
    branch := ""

    __New(name := "Benchmark"){
        this.commit := this.Cmd("git rev-parse HEAD")
        this.shortcommit := this.Cmd("git rev-parse --short HEAD")
        this.branch := this.Cmd("git branch --show-current")

        filepath := Format("{1}\{2}-{3}.md", A_ScriptDir, String(name), this.shortcommit)
        this.outputFile := FileOpen(filepath, "w")

        this.outputFile.WriteLine("# Performance Benchmarks - " . this.shortcommit)
        this.outputFile.WriteLine("### Benchmarks run:")
        this.outputFile.WriteLine("- On branch: " . this.branch)
        this.outputFile.WriteLine("- On commit: " . this.commit)

        this.watch := Stopwatch()
    }

    __Delete(){
        this.outputFile.Close()
    }

    /**
     * Add a header to the benchmark report
     * @param {String} text text of the header 
     */
    RptHeader(text){
        this.outputFile.WriteLine()
        this.outputFile.WriteLine("### " . text)
        this.WriteTableHeaders()
    }

    /**
     * Re-Add the table header (e.g. after adding some text in between)
     */
    WriteTableHeaders(){
        this.outputFile.WriteLine(Format("| Test Name                                                      | Min (ms)| Max (ms)| Avg (ms)| Runs    |"))
        this.outputFile.WriteLine(Format("|:---------------------------------------------------------------|:-------:|:-------:|:-------:|:-------:|"))
    }

    /**
     * Adds a report row for times
     * @param {Array<Number>} times Array of times to add 
     */
    RptAddTimes(name, times){
        minTime := 99999999, maxTime := -999999999, total := 0
        for(time in times){
            minTime := Min(time, minTime)
            maxTime := Max(time, maxTime)
            total += time
        }

        this.outputFile.WriteLine(Format("|{1:-64}|{2:9.8}|{3:9.8}|{4:9.8}|{5:9}|", name, minTime * 1000, maxTime * 1000, (total / times.Length) * 1000, times.Length))
    }

    /**
     * Benchmark some function
     * @param {String} name Name for the test (max 64 characters)
     * @param {Func (*) => Any} function The function to benchmark
     * @param {Integer} iterations number if times to run the benchmark 
     */
    Run(name, function, iterations := 1000){
        times := Array(), times.Length := iterations
        Loop(iterations){
            times[A_Index] := this.watch.Time(function)
        }

        this.RptAddTimes(name, times)
    }

    /**
     * Run a benchmark that includes a reset function
     * @param {String} name Name for the test (max 64 characters)
     * @param {Func (*) => Any} function The function to benchmark
     * @param {Func (*) => Any} resetFunction Function to run after the benchmarked function to reset
     * @param {Integer} iterations number if times to run the benchmark 
     */
    RunWithReset(name, function, resetFunction, iterations := 1000){
        times := Array(), times.Length := iterations
        Loop(iterations){
            times[A_Index] := this.watch.Time(function)
            resetFunction.Call()
        }

        this.RptAddTimes(name, times)
    }

    /**
     * Run a command and return its output
     * @param {String} cmd command to run 
     */
    Cmd(cmd){
        tmpFile := A_ScriptDir . "\HEAD"
        RunWait(Format('{1} /c "{3} 1>{2}"', A_ComSpec, tmpFile, cmd), , "Hide")

        output := Trim(FileRead(tmpFile), "`t`r`n ")
        FileDelete(tmpFile)

        return output
    }
}

