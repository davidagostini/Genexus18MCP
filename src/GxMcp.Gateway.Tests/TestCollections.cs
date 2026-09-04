using Xunit;

// Gateway tests share process-wide configuration and environment variables
// (for example GXMCP_TERSE and GXMCP_NO_STRUCTURED_CONTENT). Serial execution
// prevents one test from changing the contract observed by another while
// retaining every test and assertion.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
