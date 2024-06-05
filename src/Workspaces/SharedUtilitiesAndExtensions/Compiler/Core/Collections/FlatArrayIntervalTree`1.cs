﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Collections.Internal;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Shared.Collections;

internal interface IIntervalTreeHelper<T, TIntervalTree, TNode>
    where TIntervalTree : IIntervalTree<T>
{
    public bool TryGetRoot(TIntervalTree tree, [NotNullWhen(true)] out TNode? root);
    public bool TryGetLeftNode(TIntervalTree tree, TNode node, [NotNullWhen(true)] out TNode? leftNode);
    public bool TryGetRightNode(TIntervalTree tree, TNode node, [NotNullWhen(true)] out TNode? rightNode);

    public T GetValue(TIntervalTree tree, TNode node);
    public TNode GetMaxEndNode(TIntervalTree tree, TNode node);
}

internal static class IntervalTreeHelpers<T, TIntervalTree, TNode, TIntervalTreeHelper>
    where TIntervalTree : IIntervalTree<T>
    where TIntervalTreeHelper : struct, IIntervalTreeHelper<T, TIntervalTree, TNode>
{
    private static readonly ObjectPool<Stack<TNode>> s_nodeStackPool = new(() => new(), 128, trimOnFree: false);

    public static IEnumerator<T> GetEnumerator(TIntervalTree tree)
    {
        var helper = default(TIntervalTreeHelper);
        if (!helper.TryGetRoot(tree, out var root))
            return SpecializedCollections.EmptyEnumerator<T>();

        return GetEnumeratorWorker(tree, helper, root);

        static IEnumerator<T> GetEnumeratorWorker(
            TIntervalTree tree, TIntervalTreeHelper helper, TNode root)
        {
            using var _ = s_nodeStackPool.GetPooledObject(out var stack);
            var current = (Node: root, HasValue: true);

            while (current.HasValue || stack.Count > 0)
            {
                // Traverse all the way down the left side of the tree, pushing nodes onto the stack as we go.
                while (current.HasValue)
                {
                    stack.Push(current.Node);
                    var leftHasValue = helper.TryGetLeftNode(tree, current.Node, out var leftNode);
                    current = (leftNode!, leftHasValue);
                }

                Contract.ThrowIfTrue(current.HasValue);
                Contract.ThrowIfTrue(stack.Count == 0);
                current = (stack.Pop(), HasValue: true);

                // We only get to a node once we've walked the left side of it.  So we can now return the parent node at
                // that point.
                yield return helper.GetValue(tree, current.Node);

                // now get the right side and set things up so we can walk into it.
                var rightHasValue = helper.TryGetRightNode(tree, current.Node, out var right);
                current = (right!, rightHasValue);
            }
        }
    }

    public static int FillWithIntervalsThatMatch<TIntrospector>(
        TIntervalTree tree, int start, int length,
        TestInterval<T, TIntrospector> testInterval,
        ref TemporaryArray<T> builder,
        in TIntrospector introspector,
        bool stopAfterFirst)
        where TIntrospector : struct, IIntervalIntrospector<T>
    {
        var helper = default(TIntervalTreeHelper);

        if (!helper.TryGetRoot(tree, out var root))
            return 0;

        using var _ = s_nodeStackPool.GetPooledObject(out var stack);
        var current = (Node: root, HasValue: true);

        var matches = 0;
        var end = start + length;

        while (current.HasValue || stack.Count > 0)
        {
            // Traverse all the way down the left side of the tree, pushing nodes onto the stack as we go.
            while (current.HasValue)
            {
                stack.Push(current.Node);
                var leftHasValue = helper.TryGetLeftNode(tree, current.Node, out var leftNode);
                current = (leftNode!, leftHasValue);
            }

            Contract.ThrowIfTrue(current.HasValue);
            Contract.ThrowIfTrue(stack.Count == 0);
            current = (stack.Pop(), HasValue: true);

            // We only get to a node once we've walked the left side of it.  So we can now process the parent node at
            // that point.

            var currentNodeValue = helper.GetValue(tree, current.Node);
            if (testInterval(currentNodeValue, start, length, in introspector))
            {
                matches++;
                builder.Add(currentNodeValue);

                if (stopAfterFirst)
                    return 1;
            }

            // now get the right side and set things up so we can walk into it.
            var rightHasValue = helper.TryGetRightNode(tree, current.Node, out var right);
            current = (right!, rightHasValue);
        }

        return matches;
    }

    public static bool Any<TIntrospector>(TIntervalTree tree, int start, int length, TestInterval<T, TIntrospector> testInterval, in TIntrospector introspector)
        where TIntrospector : struct, IIntervalIntrospector<T>
    {
        // Inlined version of FillWithIntervalsThatMatch, optimized to do less work and stop once it finds a match.

        var helper = default(TIntervalTreeHelper);
        if (!helper.TryGetRoot(tree, out var root))
            return false;

        using var _ = s_nodeStackPool.GetPooledObject(out var candidates);

        var end = start + length;

        candidates.Push(root);

        while (candidates.TryPop(out var currentNode))
        {
            // Check the nodes as we go down.  That way we can stop immediately when we find something that matches,
            // instead of having to do an entire in-order walk, which might end up hitting a lot of nodes we don't care
            // about and placing a lot into the stack.
            if (testInterval(helper.GetValue(tree, currentNode), start, length, in introspector))
                return true;

            if (ShouldExamineRight(tree, start, end, currentNode, in introspector, out var right))
                candidates.Push(right);

            if (ShouldExamineLeft(tree, start, currentNode, in introspector, out var left))
                candidates.Push(left);
        }

        return false;
    }

    private static bool ShouldExamineRight<TIntrospector>(
        TIntervalTree tree,
        int start,
        int end,
        TNode currentNode,
        in TIntrospector introspector,
        [NotNullWhen(true)] out TNode? right) where TIntrospector : struct, IIntervalIntrospector<T>
    {
        var helper = default(TIntervalTreeHelper);

        // right children's starts will never be to the left of the parent's start so we should consider right
        // subtree only if root's start overlaps with interval's End, 
        if (introspector.GetSpan(helper.GetValue(tree, currentNode)).Start <= end)
        {
            if (helper.TryGetRightNode(tree, currentNode, out var rightNode) &&
                GetEnd(helper.GetValue(tree, helper.GetMaxEndNode(tree, rightNode)), in introspector) >= start)
            {
                right = rightNode;
                return true;
            }
        }

        right = default;
        return false;
    }

    private static bool ShouldExamineLeft<TIntrospector>(
        TIntervalTree tree,
        int start,
        TNode currentNode,
        in TIntrospector introspector,
        [NotNullWhen(true)] out TNode? left) where TIntrospector : struct, IIntervalIntrospector<T>
    {
        var helper = default(TIntervalTreeHelper);
        // only if left's maxVal overlaps with interval's start, we should consider 
        // left subtree
        if (helper.TryGetLeftNode(tree, currentNode, out left) &&
            GetEnd(helper.GetValue(tree, helper.GetMaxEndNode(tree, left)), in introspector) >= start)
        {
            return true;
        }

        return false;
    }

    private static int GetEnd<TIntrospector>(T value, in TIntrospector introspector)
        where TIntrospector : struct, IIntervalIntrospector<T>
        => introspector.GetSpan(value).End;
}

/// <summary>
/// Implementation of an <see cref="IIntervalTree{T}"/> backed by a contiguous array of values.  This is a more memory
/// efficient way to store an interval tree than the traditional binary tree approach.  This should be used when the 
/// values of the interval tree are known up front and will not change after the tree is created.
/// </summary>
/// <typeparam name="T"></typeparam>
internal readonly struct FlatArrayIntervalTree<T> : IIntervalTree<T>
{
    private readonly record struct Node(T Value, int MaxEndNodeIndex);

    public static readonly FlatArrayIntervalTree<T> Empty = new(new SegmentedArray<Node>(0));

    /// <summary>
    /// The nodes of this interval tree flatted into a single array.  The root is as index 0.  The left child of any
    /// node at index <c>i</c> is at <c>2*i + 1</c> and the right child is at <c>2*i + 2</c>. If a left/right child
    /// index is beyond the length of this array, that is equivalent to that node not having such a child.
    /// </summary>
    /// <remarks>
    /// The binary tree we represent here is a *complete* binary tree (not to be confused with a *perfect* binary tree).
    /// A complete binary tree is a binary tree in which every level, except possibly the last, is completely filled,
    /// and all nodes in the last level are as far left as possible. 
    /// </remarks>
    private readonly SegmentedArray<Node> _array;

    private FlatArrayIntervalTree(SegmentedArray<Node> array)
        => _array = array;

    /// <summary>
    /// Provides access to lots of common algorithms on this interval tree.
    /// </summary>
    public IntervalTreeAlgorithms<T, FlatArrayIntervalTree<T>> Algorithms => new(this);

    /// <summary>
    /// Creates a <see cref="FlatArrayIntervalTree{T}"/> from an unsorted list of <paramref name="values"/>.  This will
    /// incur a delegate allocation to sort the values.  If callers can avoid that allocation by pre-sorting the values,
    /// they should do so and call <see cref="CreateFromSorted"/> instead.
    /// </summary>
    /// <remarks>
    /// <paramref name="values"/> will be sorted in place.
    /// </remarks>
    public static FlatArrayIntervalTree<T> CreateFromUnsorted<TIntrospector>(in TIntrospector introspector, SegmentedList<T> values)
        where TIntrospector : struct, IIntervalIntrospector<T>
    {
        var localIntrospector = introspector;
        values.Sort((t1, t2) => localIntrospector.GetSpan(t1).Start - localIntrospector.GetSpan(t2).Start);
        return CreateFromSorted(introspector, values);
    }

    /// <summary>
    /// Creates an interval tree from a sorted list of values.  This is more efficient than creating from an unsorted
    /// list as building doesn't need to figure out where the nodes need to go n-log(n) and doesn't have to rebalance
    /// anything (again, another n-log(n) operation).  Rebalancing is particularly expensive as it involves tons of
    /// pointer chasing operations, which is both slow, and which impacts the GC which has to track all those writes.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">The values must be sorted such that given any two elements 'a' and 'b' in the list, if 'a'
    /// comes before 'b' in the list, then it's "start position" (as determined by the introspector) must be less than
    /// or equal to 'b's start position.  This is a requirement for the algorithm to work correctly.
    /// </list>
    /// <list type="bullet">The <paramref name="values"/> list will be mutated as part of this operation.
    /// </list>
    /// </remarks>
    public static FlatArrayIntervalTree<T> CreateFromSorted<TIntrospector>(in TIntrospector introspector, SegmentedList<T> values)
        where TIntrospector : struct, IIntervalIntrospector<T>
    {
#if DEBUG
        var localIntrospector = introspector;
        Debug.Assert(values.IsSorted(Comparer<T>.Create((t1, t2) => localIntrospector.GetSpan(t1).Start - localIntrospector.GetSpan(t2).Start)));
#endif

        if (values.Count == 0)
            return Empty;

        // Create the array to sort the binary search tree nodes in.
        var array = new SegmentedArray<Node>(values.Count);

        // Place the values into the array in a way that will create a complete binary tree.
        BuildCompleteTreeTop(values, array);

        // Next, do a pass over the entire tree, updating each node to point at the max end node in its subtree.
        ComputeMaxEndNodes(array, 0, in introspector);

        return new FlatArrayIntervalTree<T>(array);

        static void BuildCompleteTreeTop(SegmentedList<T> source, SegmentedArray<Node> destination)
        {
            // The nature of a complete tree is that the last level always only contains the elements at the even
            // indices of the original source. For example, given the initial values a-n:
            // 
            // a, b, c, d, e, f, g, h, i, j, k, l, m, n.  The final tree will look like:
            // h, d, l, b, f, j, n, a, c, e, g, i, k, m.  Which corresponds to:
            //
            //           h
            //        /     \
            //       d       l
            //      / \     / \
            //     b   f   j   n
            //    / \ / \ / \ /
            //    a c e g i k m
            //
            // Note that the first 3 levels are the elements at the odd indices of the original list) which end up
            // forming a perfect balanced tree, and the elements at the even indices of the original list are the
            // remaining values on the last level.

            // How many levels will be in the perfect binary tree.  For the example above, this would be 3. 
            var level = SegmentedArraySortUtils.Log2((uint)source.Count + 1);

            // How many extra elements will be on the last level of the binary tree (if this is not a perfect tree).
            // For the example above, this is 7.
            var extraElementsCount = source.Count - ((1 << level) - 1);

            if (extraElementsCount > 0)
            {
                // Where at the end to start swapping elements from.  In the above example, this would be 12.
                var lastElementToSwap = extraElementsCount * 2 - 2;

                for (int i = lastElementToSwap, j = 0; i > 1; i -= 2, j++)
                {
                    var destinationIndex = destination.Length - 1 - j;
                    destination[destinationIndex] = new Node(source[i], MaxEndNodeIndex: destinationIndex);
                    source[lastElementToSwap - j] = source[i - 1];

                    // The above loop will do the following over the first few iterations (changes highlighted with *):
                    //
                    // Dst: ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞,   ∞, *m* // m placed at the end of the destination.
                    // Src: a, b, c, d, e, f, g, h, i, j, k, l, *l*,   n // l moved to where m was in the original source.
                    //
                    // Dst: ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞,   ∞, *k*, m // k placed right before m in the destination.
                    // Src: a, b, c, d, e, f, g, h, i, j, k, *j*,   l, n // j moved right before where we placed l in the original source.
                    //
                    // Dst: ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞,   ∞, *i*, k, m // i placed right before k in the destination.
                    // Src: a, b, c, d, e, f, g, h, i, j, *h*,   j, l, n // h moved right before where we placed j in the original source.
                    //
                    // Each iteration takes the next element at an even index from the end of the source list and places
                    // it at the next available space from the end of the destination array (effectively building the
                    // last row of the complete binary tree).
                    //
                    // It then takes the next element at an odd index from the end of the source list and moves it to
                    // the next spot from the end of the source list.  This makes the end of the source-list contain the
                    // original odd-indexed elements (up the perfect-complete count of elements), now abutted against
                    // each other.
                }

                // After the loop above fully completes, source will be equal to:
                //
                // a, b, c, d, e, f, g - b, d, f, h, j, l, n.
                //
                // The last half (after 'g') will be updated to be the odd-indexed elements from the original list.
                // This will be what we'll create the perfect tree from below.  We will not look at the elements before
                // this in 'source' as they are already either in the correct place in the 'source' *or* 'destination'
                // arrays.
                //
                // Destination will be equal to:
                // ∞, ∞, ∞, ∞, ∞, ∞, ∞, ∞, c, e, g, i, k, m
                //
                // which is the elements at the original even indices from the original list.

                // The above loop will not hit the first element in the list (since we do not want to do a swap for the
                // root element).  So we have to handle this case specially at the end.
                var firstOddIndex = destination.Length - extraElementsCount;
                destination[firstOddIndex] = new Node(source[0], MaxEndNodeIndex: firstOddIndex);
                // Destination will be equal to:
                // ∞, ∞, ∞, ∞, ∞, ∞, ∞, a, c, e, g, i, k, m
            }

            // Recursively build the perfect balanced subtree from the remaining elements, storing them into the start
            // of the array.  In the above example, this is building the perfect balanced tree for the elements
            // b, d, f, h, j, l, n.
            BuildCompleteTreeRecursive(
                source, destination, startInclusive: extraElementsCount, endExclusive: source.Count, destinationIndex: 0);
        }

        static void BuildCompleteTreeRecursive(
            SegmentedList<T> source,
            SegmentedArray<Node> destination,
            int startInclusive,
            int endExclusive,
            int destinationIndex)
        {
            if (startInclusive >= endExclusive)
                return;

            var midPoint = (startInclusive + endExclusive) / 2;
            destination[destinationIndex] = new Node(source[midPoint], MaxEndNodeIndex: destinationIndex);

            BuildCompleteTreeRecursive(source, destination, startInclusive, midPoint, GetLeftChildIndex(destinationIndex));
            BuildCompleteTreeRecursive(source, destination, midPoint + 1, endExclusive, GetRightChildIndex(destinationIndex));
        }

        // Returns the max end *position* of tree rooted at currentNodeIndex.  If there is no tree here (it refers to a
        // null child), then this will return -1;
        static int ComputeMaxEndNodes(SegmentedArray<Node> array, int currentNodeIndex, in TIntrospector introspector)
        {
            if (currentNodeIndex >= array.Length)
                return -1;

            var leftChildIndex = GetLeftChildIndex(currentNodeIndex);
            var rightChildIndex = GetRightChildIndex(currentNodeIndex);

            // ensure the left and right trees have their max end nodes computed first.
            var leftMaxEndValue = ComputeMaxEndNodes(array, leftChildIndex, in introspector);
            var rightMaxEndValue = ComputeMaxEndNodes(array, rightChildIndex, in introspector);

            // Now get the max end of the left and right children and compare to our end.  Whichever is the rightmost
            // endpoint is considered the max end index.
            var currentNode = array[currentNodeIndex];
            var thisEndValue = introspector.GetSpan(currentNode.Value).End;

            if (thisEndValue >= leftMaxEndValue && thisEndValue >= rightMaxEndValue)
            {
                // The root's end was further to the right than both the left subtree and the right subtree. No need to
                // change it as that is what we store by default for any node.
                return thisEndValue;
            }

            // One of the left or right subtrees went further to the right.
            Contract.ThrowIfTrue(leftMaxEndValue < 0 && rightMaxEndValue < 0);

            if (leftMaxEndValue >= rightMaxEndValue)
            {
                // Set this node's max end to be the left subtree's max end.
                var maxEndNodeIndex = array[leftChildIndex].MaxEndNodeIndex;
                array[currentNodeIndex] = new Node(currentNode.Value, maxEndNodeIndex);
                return leftMaxEndValue;
            }
            else
            {
                Contract.ThrowIfFalse(rightMaxEndValue > leftMaxEndValue);

                // Set this node's max end to be the right subtree's max end.
                var maxEndNodeIndex = array[rightChildIndex].MaxEndNodeIndex;
                array[currentNodeIndex] = new Node(currentNode.Value, maxEndNodeIndex);
                return rightMaxEndValue;
            }
        }
    }

    private static int GetLeftChildIndex(int nodeIndex)
        => (2 * nodeIndex) + 1;

    private static int GetRightChildIndex(int nodeIndex)
        => (2 * nodeIndex) + 2;

    bool IIntervalTree<T>.Any<TIntrospector>(int start, int length, TestInterval<T, TIntrospector> testInterval, in TIntrospector introspector)
        => IntervalTreeHelpers<T, FlatArrayIntervalTree<T>, /*TNode*/ int, FlatArrayIntervalTreeHelper>.Any(this, start, length, testInterval, in introspector);

    int IIntervalTree<T>.FillWithIntervalsThatMatch<TIntrospector>(
        int start, int length, TestInterval<T, TIntrospector> testInterval,
        ref TemporaryArray<T> builder, in TIntrospector introspector,
        bool stopAfterFirst)
    {
        return IntervalTreeHelpers<T, FlatArrayIntervalTree<T>, /*TNode*/ int, FlatArrayIntervalTreeHelper>.FillWithIntervalsThatMatch(
            this, start, length, testInterval, ref builder, in introspector, stopAfterFirst);
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public IEnumerator<T> GetEnumerator()
        => IntervalTreeHelpers<T, FlatArrayIntervalTree<T>, /*TNode*/ int, FlatArrayIntervalTreeHelper>.GetEnumerator(this);

    private readonly struct FlatArrayIntervalTreeHelper : IIntervalTreeHelper<T, FlatArrayIntervalTree<T>, int>
    {
        public T GetValue(FlatArrayIntervalTree<T> tree, int node)
            => tree._array[node].Value;

        public int GetMaxEndNode(FlatArrayIntervalTree<T> tree, int node)
            => tree._array[node].MaxEndNodeIndex;

        public bool TryGetRoot(FlatArrayIntervalTree<T> tree, out int root)
        {
            root = 0;
            return tree._array.Length > 0;
        }

        public bool TryGetLeftNode(FlatArrayIntervalTree<T> tree, int node, out int leftNode)
        {
            leftNode = GetLeftChildIndex(node);
            return leftNode < tree._array.Length;
        }

        public bool TryGetRightNode(FlatArrayIntervalTree<T> tree, int node, out int rightNode)
        {
            rightNode = GetRightChildIndex(node);
            return rightNode < tree._array.Length;
        }
    }
}
