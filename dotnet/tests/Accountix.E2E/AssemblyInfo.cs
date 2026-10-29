// One browser, one server port, one database copy: run the tests one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
