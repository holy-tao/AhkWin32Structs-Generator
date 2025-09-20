#Requires AutoHotkey v2.0

/**
 * Simple wrapper for QueryPerformanceTimer
 * @see https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps
 * @author Tao Beloney
 */
class Stopwatch extends Object {

    /**
     * The frequency of the performance counter in counts per second
     * @type {Integer}
     */
    frequency := 0

    /**
     * Is the timer running?
     * @type {Boolean}
     */
    isRunning := false

    /**
     * @private the time the stopwatch started
     * @type {Integer}
     */
    _startTime := 0

    /**
     * Creates a new stopwatch
     */
    __New(){
        DllCall("QueryPerformanceFrequency", "Int64*", &freq := 0)
        this.DefineProp("frequency", {Get: (*) => freq})
    }

    /**
     * Start the stopwatch
     */
    Start(){
        if(this.isRunning)
            throw Error("Stopwatch is already running")

        DllCall("QueryPerformanceCounter", "Int64*", &counter := 0)
        this._startTime := counter
        this.isRunning := true
    }

    /**
     * Gets a "lap" from the stopwatch, that is, gets the time since it started
     * but does not restart the timer
     * @returns {Integer} The number of seconds since the stopwatch was started
     */
    Lap(){
        if(!this.isRunning)
            throw Error("Stopwatch is not running")

        DllCall("QueryPerformanceCounter", "Int64*", &counter := 0)
        return (counter - this._startTime) / this.frequency
    }

    /**
     * Stops the stopwatch and returns the amount of time it was running for
     * @returns {Integer} The number of seconds since the stopwatch was started
     */
    Stop(){
        if(!this.isRunning)
            throw Error("Stopwatch is not running")

        DllCall("QueryPerformanceCounter", "Int64*", &counter := 0)
        this.isRunning := false
        return (counter - this._startTime) / this.frequency
    }

    /**
     * Times a function. Its return value is ignored
     * @param {func (*) => Any} function The function to time
     * @returns {Integer} the number of seconds that `function` took to run
     */
    Time(function){
        if(this.isRunning)
            throw Error("Stopwatch is already running")

        this.Start()
        function.Call()
        return this.Stop()
    }

    /**
     * Times the execution of a function, either until it finishes OR until it errors out.
     * Note that this function introduces some overhead due to the try/catch block that is
     * not present in `Time()`, especially in the case where an error is actually thrown
     * 
     * @param {func (*) => Any} function The function to time 
     * @param {Error} err an output variable in which to store the thrown error, if any
     * @returns {Integer} the number of seconds that `function` took to finish or error
     */
    TimeIgnoreErrors(function, &err?){
        if(this.isRunning)
            throw Error("Stopwatch is already running")

        time := 0

        try{
            this.Start()
            function.Call()
        }
        catch Any as thrown{
            if(IsSet(err))
                err := thrown
        }
        finally{
            time := this.Stop()
        }

        return time
    }
}