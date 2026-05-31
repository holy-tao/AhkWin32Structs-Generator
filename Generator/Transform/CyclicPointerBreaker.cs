namespace AhkWin32.Generator.Transform;

using System.Diagnostics;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Detects struct import cycles and marks the cuttable pointer edges so the emitter can break
/// them with lazy accessors.
///
/// <para>
/// Under v2.1, a struct's class initialization eagerly evaluates the type specifier of every
/// field, including <c>X.Ptr</c> pointer fields. When two structs reference each other and at
/// least one embeds the other <i>by value</i>, eagerly evaluating the pointer side deadlocks
/// against the value side being mid-construction, producing a fatal, uncatchable
/// "Cannot add typed property" load error.
/// </para>
///
/// <para>
/// A pure value-embed cycle is impossible (it would have infinite size), so every struct cycle
/// contains at least one pointer edge. A pointer is always <c>A_PtrSize</c> regardless of its
/// pointee (the C "forward declaration" principle), so making pointer fields resolve their
/// pointee lazily (at call time, not class-init time) is always sufficient to break the cycle.
/// This pass builds the load-time struct dependency graph, finds strongly-connected components,
/// and flags every pointer-to-struct field whose pointee is in the same non-trivial SCC via
/// <see cref="FieldMember.EmitAsLazyPointer"/>.
/// </para>
/// </summary>
public sealed class CyclicPointerBreaker(ILogger<CyclicPointerBreaker> logger)
{
    private readonly ILogger<CyclicPointerBreaker> _logger = logger;

    /// <summary>A pointer-to-struct field that could be cut to break a cycle.</summary>
    private readonly record struct SoftEdge(FieldMember Field, string OwnerFqn, string PointeeFqn);

    public void Apply(TypeRegistry registry)
    {
        var watch = Stopwatch.StartNew();

        // Adjacency over struct FQNs (hard value-embed edges + soft pointer edges), plus the list
        // of soft edges with their owning FieldMember so we can mark them once SCCs are known.
        var graph = new Dictionary<string, HashSet<string>>();
        var softEdges = new List<SoftEdge>();

        foreach (StructType structType in registry.GetAll<StructType>())
        {
            string ownerFqn = structType.FQN;
            graph.TryAdd(ownerFqn, []);
            CollectEdges(registry, structType, ownerFqn, structType.Members, graph, softEdges);
        }

        if (softEdges.Count == 0)
            return;

        // Strongly-connected components over the combined graph.
        Dictionary<string, int> sccId = ComputeSccs(graph, out int[] sccSize);

        // Mark soft edges that stay within a single non-trivial SCC. Track the SCCs we actually
        // broke this way so we can warn about any nontrivial SCC left with no scalar soft edge.
        int markedFields = 0;
        var brokenSccs = new HashSet<int>();
        var cyclicSoftSccs = new HashSet<int>();

        foreach (SoftEdge edge in softEdges)
        {
            if (
                !sccId.TryGetValue(edge.OwnerFqn, out int owner) || !sccId.TryGetValue(edge.PointeeFqn, out int pointee)
            )
                continue;
            if (owner != pointee || sccSize[owner] <= 1)
                continue;

            cyclicSoftSccs.Add(owner);
            if (!edge.Field.EmitAsLazyPointer)
            {
                _logger.LogTrace(
                    "Marking {ownerFqn}.{field} ({pointerFqn}*) as a lazy pointer",
                    edge.OwnerFqn,
                    edge.Field.Name,
                    edge.PointeeFqn
                );
                edge.Field.EmitAsLazyPointer = true;
                markedFields++;
            }
            brokenSccs.Add(owner);
        }

        watch.Stop();

        _logger.LogInformation(
            "Marked {FieldCount} pointer field(s) lazy across {SccCount} cyclic struct cluster(s) in {elapsed:F1}s",
            markedFields,
            brokenSccs.Count,
            watch.Elapsed.TotalSeconds
        );

        WarnUnbrokenCycles(graph, sccId, sccSize, cyclicSoftSccs);
    }

    /// <summary>
    /// Walk a struct's fields (recursing through embedded nested structs, whose field type
    /// specifiers are also evaluated during the enclosing struct's class init) and record hard
    /// (value-embed) and soft (pointer-to-struct) edges to other registered structs.
    /// </summary>
    private static void CollectEdges(
        TypeRegistry registry,
        StructType owner,
        string ownerFqn,
        IReadOnlyList<FieldMember> fields,
        Dictionary<string, HashSet<string>> graph,
        List<SoftEdge> softEdges
    )
    {
        foreach (FieldMember field in fields)
        {
            // Nested struct definitions are emitted inside the enclosing class, so their field
            // type specifiers run during the enclosing struct's init too — recurse for edges.
            if (field.EmbeddedStruct is not null)
                CollectEdges(registry, owner, ownerFqn, field.EmbeddedStruct.Members, graph, softEdges);

            switch (field.Type)
            {
                // Soft edge: pointer-to-struct. Cuttable via a lazy accessor.
                case PointerType { Pointee: StructRef ptrSr } when IsRegisteredStruct(registry, ptrSr.FQN):
                    AddEdge(graph, ownerFqn, ptrSr.FQN);
                    if (ptrSr.FQN != ownerFqn)
                        softEdges.Add(new SoftEdge(field, ownerFqn, ptrSr.FQN));
                    break;

                // Hard edge: value-embedded struct (scalar or array). Cannot be cut.
                case StructRef sr when IsRegisteredStruct(registry, sr.FQN):
                    AddEdge(graph, ownerFqn, sr.FQN);
                    break;

                case ArrayType { ElementType: StructRef arrSr } when IsRegisteredStruct(registry, arrSr.FQN):
                    AddEdge(graph, ownerFqn, arrSr.FQN);
                    break;

                // Array-of-pointer-to-struct is a soft edge in principle, but lazy emission for
                // arrays is out of scope for v1. Still record the dependency so SCCs are accurate;
                // WarnUnbrokenCycles surfaces any cluster that only such edges could break.
                case ArrayType { ElementType: PointerType { Pointee: StructRef arrPtrSr } }
                    when IsRegisteredStruct(registry, arrPtrSr.FQN):
                    AddEdge(graph, ownerFqn, arrPtrSr.FQN);
                    break;
            }
        }
    }

    private static bool IsRegisteredStruct(TypeRegistry registry, string fqn) => registry.Contains<StructType>(fqn);

    private static void AddEdge(Dictionary<string, HashSet<string>> graph, string from, string to)
    {
        if (from == to)
            return; // self edges never deadlock and never form a nontrivial SCC
        if (!graph.TryGetValue(from, out HashSet<string>? targets))
            graph[from] = targets = [];
        targets.Add(to);
        graph.TryAdd(to, []);
    }

    /// <summary>
    /// Iterative <a href="https://en.wikipedia.org/wiki/Tarjan%27s_strongly_connected_components_algorithm">Tarjan strongly-connected-components</a>.
    /// Iterative to avoid stack overflow on the long dependency chains present in the metadata. Returns a node -&gt;
    /// component-id map and fills <paramref name="sccSize"/> indexed by component id.
    /// </summary>
    private static Dictionary<string, int> ComputeSccs(Dictionary<string, HashSet<string>> graph, out int[] sccSize)
    {
        var index = new Dictionary<string, int>();
        var lowlink = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        var tarjanStack = new Stack<string>();
        var component = new Dictionary<string, int>();
        var sizes = new List<int>();
        int nextIndex = 0;
        int nextScc = 0;

        // Each work item is a node plus an enumerator cursor over its successors.
        foreach (string root in graph.Keys)
        {
            if (index.ContainsKey(root))
                continue;

            var callStack = new Stack<(string Node, List<string> Succ, int Cursor)>();
            index[root] = lowlink[root] = nextIndex++;
            tarjanStack.Push(root);
            onStack.Add(root);
            callStack.Push((root, [.. graph[root]], 0));

            while (callStack.Count > 0)
            {
                (string node, List<string> succ, int cursor) = callStack.Pop();

                bool recursed = false;
                while (cursor < succ.Count)
                {
                    string w = succ[cursor];
                    cursor++;
                    if (!index.ContainsKey(w))
                    {
                        // "Recurse" into w: save current frame, push child frame.
                        callStack.Push((node, succ, cursor));
                        index[w] = lowlink[w] = nextIndex++;
                        tarjanStack.Push(w);
                        onStack.Add(w);
                        callStack.Push((w, graph.TryGetValue(w, out HashSet<string>? ws) ? [.. ws] : [], 0));
                        recursed = true;
                        break;
                    }
                    else if (onStack.Contains(w))
                    {
                        lowlink[node] = Math.Min(lowlink[node], index[w]);
                    }
                }

                if (recursed)
                    continue;

                // Done with node: if it's a root of an SCC, pop the component.
                if (lowlink[node] == index[node])
                {
                    int size = 0;
                    while (true)
                    {
                        string member = tarjanStack.Pop();
                        onStack.Remove(member);
                        component[member] = nextScc;
                        size++;
                        if (member == node)
                            break;
                    }
                    sizes.Add(size);
                    nextScc++;
                }

                // Propagate lowlink to the parent frame (the next item on the call stack).
                if (callStack.Count > 0)
                {
                    (string parent, List<string> psucc, int pcursor) = callStack.Pop();
                    lowlink[parent] = Math.Min(lowlink[parent], lowlink[node]);
                    callStack.Push((parent, psucc, pcursor));
                }
            }
        }

        sccSize = [.. sizes];
        return component;
    }

    /// <summary>
    /// Warn about any nontrivial SCC that no scalar soft edge could break (e.g. its only pointer
    /// edges are array-of-pointer, currently unhandled). Such a cluster may still fail to load.
    /// </summary>
    private void WarnUnbrokenCycles(
        Dictionary<string, HashSet<string>> graph,
        Dictionary<string, int> sccId,
        int[] sccSize,
        HashSet<int> brokenSccs
    )
    {
        // Group nodes by SCC for nontrivial components only.
        var membersByScc = new Dictionary<int, List<string>>();
        foreach ((string fqn, int id) in sccId)
        {
            if (sccSize[id] <= 1)
                continue;
            if (!membersByScc.TryGetValue(id, out List<string>? members))
                membersByScc[id] = members = [];
            members.Add(fqn);
        }

        foreach ((int id, List<string> members) in membersByScc)
        {
            if (brokenSccs.Contains(id))
            {
                _logger.LogDebug(
                    "Cyclic struct cluster ({Size}): {Members}",
                    members.Count,
                    string.Join(", ", members)
                );
                continue;
            }

            _logger.LogWarning(
                "Cyclic struct cluster of {Size} has no scalar pointer edge to cut and may fail to load: {Members}",
                members.Count,
                string.Join(", ", members)
            );
        }
    }
}
