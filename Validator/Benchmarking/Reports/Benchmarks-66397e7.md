# Performance Benchmarks - 66397e7
### Benchmarks run:
- On branch: feature/benchmarking
- On commit: 66397e71c2a415a4ce4a245864801b374c66be45

### Basic Tests
| Test Name                                                      | Min (ms)| Max (ms)| Avg (ms)| Runs    |
|:---------------------------------------------------------------|:-------:|:-------:|:-------:|:-------:|
|Allocate & Deallocate DECIMAL (16 bytes)                        | 0.002500| 0.423999| 0.002829|    10000|
|Set Integer                                                     | 0.001700| 0.044600| 0.002036|    10000|
|Get Integer                                                     | 0.001700|   0.0144| 0.002055|    10000|
|Postincrement Struct Integer Property                           | 0.002099| 0.024300| 0.002744|    10000|
|Add-Assign Operator (+=)                                        | 0.002700| 0.057299| 0.003359|    10000|
|Allocate & Deallocate DISPLAY_DEVICEW (840 bytes)               | 0.003199| 0.050200| 0.003640|    10000|
|Allocate & Deallocate ENUMTYPEW (96 bytes)                      |   0.0033| 4.255899| 0.005420|    10000|

### String Tests
| Test Name                                                      | Min (ms)| Max (ms)| Avg (ms)| Runs    |
|:---------------------------------------------------------------|:-------:|:-------:|:-------:|:-------:|
|Set String Value ("test test")                                  |   0.0018| 0.048300| 0.002798|    10000|
|Get String Value                                                |   0.0018|   0.1341| 0.002510|    10000|
|String Append (.= "test")                                       | 0.002200| 0.578100| 0.003057|    10000|

### DllCall Tests
| Test Name                                                      | Min (ms)| Max (ms)| Avg (ms)| Runs    |
|:---------------------------------------------------------------|:-------:|:-------:|:-------:|:-------:|
|Wrapper function, dll not loaded (BCryptPrimitives\ProcessPrng) |   0.0014| 0.113599| 0.002082|    10000|
|Direct DllCall, dll not loaded (BCryptPrimitives\ProcessPrng)   | 0.001700|   0.0117| 0.001968|    10000|
|Wrapper function, dll loaded (BCryptPrimitives\ProcessPrng)     |   0.0014|   0.0344| 0.001832|    10000|
|Direct DllCall, dll loaded (BCryptPrimitives\ProcessPrng)       | 0.001700|   0.0557| 0.002170|    10000|
