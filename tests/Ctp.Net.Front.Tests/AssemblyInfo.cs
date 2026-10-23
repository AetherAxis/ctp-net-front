// native CTP SDK 进程内全局态——测试类并行会互踩（E2E 闪烁实证）。串行执行。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
