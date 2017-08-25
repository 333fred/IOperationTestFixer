# IOperation Test Fixer

Test fixer for IOperation tests generated with the test generator. Run when an interface is updated and a large number of tests need to be updated to a new format. Steps:
1. Use @Pilchie's test runner to run all VB and C# semantic tests.
2. Copy the output from the failed tests into a file.
3. Run IOperationTestFixer to update the newly-failing tests. Syntax: `IOperationTestFixer <path to failure file> <path to Compilers.sln>`