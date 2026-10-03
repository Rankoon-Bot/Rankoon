using System.Reflection;
using Discord;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class LevelRoleReliabilityTests
{
    [Fact]
    public async Task Repeated_synchronization_converges_without_repeating_changes_or_touching_unrelated_roles()
    {
        var state = new MemberState(99, 2);
        LevelRole[] rules = [new() { RoleId = 1, Level = 1 }, new() { RoleId = 2, Level = 10 }];
        var first = await Sync(state, rules, 5);
        var second = await Sync(state, rules, 5);
        Assert.Equal(1UL, Assert.Single(first.Added).RoleId);
        Assert.Equal(2UL, Assert.Single(first.Removed).RoleId);
        Assert.Empty(second.Added);
        Assert.Empty(second.Removed);
        Assert.Equal(1UL, Assert.Single(second.AlreadyPresent).RoleId);
        Assert.Contains(99UL, state.Roles);
        Assert.Equal(2, state.Requests);
    }

    [Fact]
    public async Task Partial_failure_is_retryable_and_successful_roles_are_not_readded()
    {
        var state = new MemberState { FailRole = 2 };
        LevelRole[] rules = [new() { RoleId = 1, Level = 1 }, new() { RoleId = 2, Level = 2 }];
        var first = await Sync(state, rules, 3);
        Assert.Single(first.Added);
        Assert.Equal("addFailed", Assert.Single(first.Failed).ErrorCode);
        state.FailRole = null;
        var retry = await Sync(state, rules, 3);
        Assert.Equal(2UL, Assert.Single(retry.Added).RoleId);
        Assert.Equal(1UL, Assert.Single(retry.AlreadyPresent).RoleId);
        Assert.Empty(retry.Failed);
    }

    [Fact]
    public async Task Duplicate_legacy_rules_use_strictest_threshold()
    {
        var state = new MemberState();
        var result = await Sync(state, [new() { RoleId = 1, Level = 1 }, new() { RoleId = 1, Level = 10 }], 5);
        Assert.Empty(result.Added);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task Deleted_role_is_reported_and_cancellation_is_not_swallowed()
    {
        var state = new MemberState();
        var result = await LevelRoleService.SynchronizeMemberAsync(state.User, [new() { RoleId = 1, Level = 1 }], 1, _ => null, default);
        Assert.Equal("roleNotFound", Assert.Single(result.Failed).ErrorCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LevelRoleService.SynchronizeMemberAsync(state.User,
            [new() { RoleId = 1, Level = 1 }], 1, Role, cancelled.Token));
    }

    [Theory]
    [InlineData(LevelTransitionStatus.Pending, -1, -1, true)]
    [InlineData(LevelTransitionStatus.RetryScheduled, 1, -1, false)]
    [InlineData(LevelTransitionStatus.RetryScheduled, -1, 1, false)]
    [InlineData(LevelTransitionStatus.Processing, -1, -1, true)]
    [InlineData(LevelTransitionStatus.Processing, -1, 1, false)]
    [InlineData(LevelTransitionStatus.Delivered, -1, -1, false)]
    [InlineData(LevelTransitionStatus.CompletedWithoutAnnouncement, -1, -1, false)]
    [InlineData(LevelTransitionStatus.DeadLetter, -1, -1, false)]
    public void Only_due_work_or_abandoned_processing_can_be_claimed(LevelTransitionStatus status, int due, int lease, bool expected)
    {
        var now = DateTime.UtcNow;
        var entry = new LevelTransitionEvent { Status = status, NextAttemptAtUtc = now.AddMinutes(due), LeaseExpiresAtUtc = now.AddMinutes(lease) };
        Assert.Equal(expected, LevelProgressionWorker.EligibleWork(now).Compile()(entry));
    }

    [Fact]
    public async Task Same_member_is_serialized_and_cancelled_waiter_does_not_release_owner()
    {
        var owner = await LevelRoleSynchronizationLock.AcquireAsync(1, 2, default);
        using var cancellation = new CancellationTokenSource();
        var waiter = LevelRoleSynchronizationLock.AcquireAsync(1, 2, cancellation.Token);
        Assert.False(waiter.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        var next = LevelRoleSynchronizationLock.AcquireAsync(1, 2, default);
        Assert.False(next.IsCompleted);
        await owner.DisposeAsync();
        await using var acquired = await next;
    }

    private static Task<LevelRoleSynchronizationResult> Sync(MemberState state, LevelRole[] rules, int level) =>
        LevelRoleService.SynchronizeMemberAsync(state.User, rules, level, Role, default);

    private static IRole Role(ulong id) => InterfaceStub.Create<IRole>((method, _) => method.Name switch
    {
        "get_Id" => id, "get_Name" => $"Role {id}", _ => throw new NotSupportedException(method.Name)
    });

    private sealed class MemberState
    {
        public HashSet<ulong> Roles { get; }
        public IGuildUser User { get; }
        public ulong? FailRole { get; set; }
        public int Requests { get; private set; }
        public MemberState(params ulong[] roles)
        {
            Roles = roles.ToHashSet();
            User = InterfaceStub.Create<IGuildUser>((method, args) =>
            {
                if (method.Name == "get_RoleIds") return Roles.ToArray();
                if (method.Name is "AddRoleAsync" or "RemoveRoleAsync")
                {
                    Requests++;
                    var id = (ulong)args![0]!;
                    if (id == FailRole) return Task.FromException(new InvalidOperationException("Missing permission"));
                    if (method.Name == "AddRoleAsync") Roles.Add(id); else Roles.Remove(id);
                    return Task.CompletedTask;
                }
                throw new NotSupportedException(method.Name);
            });
        }
    }

    public class InterfaceStub : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> invoke = null!;
        public static T Create<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
        {
            var proxy = Create<T, InterfaceStub>();
            ((InterfaceStub)(object)proxy).invoke = invoke;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => invoke(targetMethod!, args);
    }
}
