using System.Text.Json.Nodes;
using Agmen.Hosting;
using Agmen.Mcp;
using Agmen.Orchestration;

namespace Agmen.Tests;

public static class SharingChecks
{
    public static void Run()
    {
        foreach (string name in new[] { "list_siblings", "message_sibling", "read_handoff" })
        {
            Check(McpTools.Allowed("tab", name), $"A hosted tab can call {name}.");
            Check(!McpTools.Allowed("none", name), $"An outside session cannot call {name}.");
        }
        var catalog = McpTools.Catalog("tab");
        JsonNode Schema(string name) => catalog.Single(tool => (string?)tool!["name"] == name)!["inputSchema"]!;
        var handoff = Schema("read_handoff");
        Check(handoff["properties"]!.AsObject().Select(p => p.Key).Order().SequenceEqual(new[] { "count", "offset" })
              && (bool?)handoff["additionalProperties"] == false, "A handoff exposes only pagination.");
        Check((bool?)Schema("spawn_child")["properties"]!["inherit_context"]!["default"] == false,
              "New children start without inherited context by default.");

        var root = new HostedTab(".", null, knownTitle: "root");
        var otherRoot = new HostedTab(".", null, knownTitle: "other-root");
        var sender = new HostedTab(".", null, knownTitle: "sender") { ParentId = root.Id };
        var recipient = new HostedTab(".", null, knownTitle: "review") { ParentId = root.Id };
        var outsider = new HostedTab(".", null, knownTitle: "other") { ParentId = otherRoot.Id };
        var grandchild = new HostedTab(".", null, knownTitle: "leaf") { ParentId = sender.Id };
        var otherGrandchild = new HostedTab(".", null, knownTitle: "leaf2") { ParentId = sender.Id };
        List<HostedTab> tabs = [root, otherRoot, sender, recipient, outsider, grandchild, otherGrandchild];

        Check(Orchestrator.SiblingsOf(tabs, sender).SequenceEqual(new[] { recipient }), "Only direct peers are listed.");
        Check(!Orchestrator.SiblingsOf(tabs, root).Any(), "Top-level tabs have no eligible peers.");
        Check(!Orchestrator.SiblingsOf(tabs, grandchild).Any(), "Children of a nested parent have no eligible peers.");
        var (to, message) = Orchestrator.PrepareSiblingMessage(tabs, sender, recipient.Id.ToString(), "Selected excerpt.");
        Check(to == recipient && message.Text == "[From your sibling, sender] Selected excerpt."
              && message.Origin == "sibling" && !message.ReportBack, "One labeled excerpt is addressed to one sibling.");
        var canDeliver = message.CanDeliver ?? throw new InvalidOperationException("Missing sibling delivery check.");
        Check(canDeliver(), "The message can be delivered while both tabs share their original root.");
        void Refused(string address, string body)
        {
            try { Orchestrator.PrepareSiblingMessage(tabs, sender, address, body); }
            catch (Orchestrator.ToolError) { return; }
            throw new InvalidOperationException($"Unexpected message accepted for '{address}'.");
        }
        Refused(outsider.Title, "Selected excerpt.");
        Refused(sender.Title, "Selected excerpt.");
        Refused(recipient.Title, "  ");
        Refused("", "Selected excerpt.");
        recipient.Enqueue(message);
        recipient.ParentId = otherRoot.Id;
        Check(!canDeliver(), "Moving the recipient invalidates delivery.");
        Check(!recipient.TryDeliver(force: true) && !recipient.HasQueued, "A moved tab discards its queued sibling message.");
        sender.ParentId = otherRoot.Id;
        Check(!canDeliver(), "Moving both tabs to a different root invalidates delivery.");
        sender.ParentId = recipient.ParentId = root.Id;
        Check(!canDeliver(), "A message becomes invalid even if both siblings move back.");
        Func<bool> FreshGuard() => Orchestrator.PrepareSiblingMessage(tabs, sender, recipient.Id.ToString(), "Excerpt.").Message.CanDeliver!;
        foreach (var moved in new[] { sender, recipient, root })
        {
            var beforeMove = FreshGuard();
            var previous = moved.ParentId;
            moved.ParentId = otherRoot.Id;
            moved.ParentId = previous;
            Check(!beforeMove(), $"A move away and back by {moved.Title} permanently invalidates the message.");
        }
        var parentMessage = Orchestrator.WatchParentRelationship(tabs, root, recipient);
        recipient.ParentId = otherRoot.Id;
        recipient.ParentId = root.Id;
        Check(!parentMessage(), "An outstanding parent task cannot send an automatic reply across a changed relationship.");
        var beforeNesting = FreshGuard();
        root.ParentId = otherRoot.Id;
        Check(!beforeNesting(), "Nesting their original root invalidates delivery.");
        root.ParentId = null;
        var beforeClose = FreshGuard();
        tabs.Remove(sender);
        Check(!beforeClose(), "Closing the sender invalidates delivery.");
        tabs.Add(sender);
        var beforeEnd = FreshGuard();
        recipient.MarkEnded(null);
        Check(!beforeEnd(), "Ending the recipient invalidates delivery.");
        Refused(recipient.Title, "Selected excerpt.");
    }

    private static void Check(bool satisfied, string description)
    {
        if (!satisfied) throw new InvalidOperationException(description);
    }
}
