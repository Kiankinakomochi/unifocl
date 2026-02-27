using Spectre.Console;

var commands = new List<CommandSpec>
{
    new("/help", "Show available commands and usage examples"),
    new("/hierarchy", "Switch to hierarchy mode"),
    new("/project", "Switch to project mode"),
    new("/inspect", "Switch to inspector mode"),
    new("/ref", "Refresh Unity snapshot indices"),
    new("/clear", "Clear the screen and redraw startup UI"),
    new("/exit", "Exit unifocl")
};

RenderStartup(commands);

while (true)
{
    var rawInput = ReadInput();
    if (rawInput is null)
    {
        AnsiConsole.MarkupLine("[grey]Input stream closed. Session ended.[/]");
        return;
    }

    var input = rawInput.Trim();

    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (input.StartsWith('/'))
    {
        var command = input.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        switch (command)
        {
            case "/help":
                RenderCommandSection(commands);
                break;
            case "/clear":
                RenderStartup(commands);
                break;
            case "/exit":
                AnsiConsole.MarkupLine("[grey]Session closed.[/]");
                return;
            default:
                ShowSuggestions(input, commands, "Command not recognized");
                break;
        }

        continue;
    }

    ShowSuggestions(input, commands, "Intent matches");
}

static string? ReadInput()
{
    if (Console.IsInputRedirected)
    {
        AnsiConsole.Markup("[bold deepskyblue1]unifocl[/] [grey]>[/] ");
        return Console.ReadLine();
    }

    return AnsiConsole.Ask<string>("[bold deepskyblue1]unifocl[/] [grey]>[/]");
}

static void RenderStartup(List<CommandSpec> commands)
{
    AnsiConsole.Clear();
    AnsiConsole.Write(
        new FigletText("unifocl")
            .LeftJustified()
            .Color(Color.DeepSkyBlue1));

    AnsiConsole.MarkupLine("[bold green]Welcome to unifocl[/]");
    AnsiConsole.WriteLine();

    var logo = """
                       .;?tXOb*&%@$$$$@%&*bOXt];.                       
                   .!/LaB$$$$%#hpm0QQ0Zph#B$$$$BaL/i.                   
                 +vk@$$@oQu\)1(/jnvccvnrf)I]\n0o@$$@kv+.                 
              "td@$$W0t})nLkWB$$$$$$$$$@Q\vhkQx)}tQW$$@dt,              
            ,u#$$BZ\]fZM$$$$$$$$$$$$$@L)XW$$$$$$WZf]|ZB$$#u,            
          't#$$8U?108$$$$$$$$$$$$$$$d(uW$$$$$$$$$$$801-Y8$$Mf.          
         iq$$$C++-$$$$$$$$$$$$$$$$8n|k$$$$$$$$$$$$$$$$b(~C$$$q~         
        (&$$*[+q%ib$$$$$$$$$$$$$$k{U@$$$$$$$$$$$$$$$$$$$q_[*$$&(        
       j@$$m:j%$$X{$$$$$$$$@@$$$C{k$$$$$$$$$$$@@$$$$$$$$$%j:Z$$@j       
      t$$$L`J$$$$B?Q$$$$$$$$$$$u(&$$$$$$$$$$$$$$$$$$$$$$$$$J^L$$$t      
     -%$$w'C$$$$$$h~*$$$$$$$$@ff@$$$BhqZ0QLCCCCCCL0Zwdh*&B$$Q`m$$B-     
    'k$$M,j$$$$$$$$Q?8$$$$$$$fr$$&Zt-/LpkkkhhhhhkdpwZ0CXvuucL-,M$$k'    
    )$$$f:8$$$$$$$$$z)%$$$$@vt*L):   .I(L#$$$$$$$$$$$$$$$@8#b0,f$$$(    
    m$$W`v$$$$$$$$$$$v(%$$$#:-^          `-vpB$$$$$$$$$$$$$$$$v`W$$w    
   ^M$$m k$$$$$$$$$$$$X)&$$#`              .n|*$$$$$$$$$$$$$$$k m$$M^   
   I%$$X`W$$$$$$$$$$$$$L{o$#`              `Wq[p$$$$$$$$$$$$$$&`Y$$%I   
   I%$$X`W$$$$$$$$$$$$$$p}wW`              `*$o}Q$$$$$$$$$$$$$&`X$$%l   
   ^M$$m k$$$$$$$$$$$$$$$#/n'              '*$$&1Y$$$$$$$$$$$$k m$$M^   
    m$$W`c$$$$$$$$$$$$$$$$@bc?^          ^-:#$$$8)X$$$$$$$$$$$c`W$$m    
    )$$$f"zLpoW%@$$$$$$$$$$$$$W0\l.   ;)L*/c@$$$$%{Y$$$$$$$$$%;t$$$)    
    'k$$M,1pQYvuvvzYJLQ0OZZZOOOZ0X)_jw&$$fr$$$$$$$&?0$$$$$$$$x"#$$k'    
     -%$$m`0$$$$B8M*ohkbddppdbbha*M@$$$@tj@$$$$$$$$o~h$$$$$$0'Z$$%-     
      /$$$L^L$$$$$$$$$$$$$$$$$$$$$$$$$%\n$$$$$$$$$$$O-%$$$$Q^U$$$t      
       j@$$O:xB$$$$$$$$$$$$$$$$$$$$$$#1Y$$$$$$$$$$$$$(v$$Bn:0$$@r       
        (&$$o]-p$$$$$$$$$$$$$$$$$$$$w[m$$$$$$$$$$$$$$oi&b??a$$&|        
         ~q$$@J~|k$$$$$$$$$$$$$$$$Bv(*$$$$$$$$$$$$$$$$}~+U@$$pi         
          .f#$$8X?)Z8$$$$$$$$$$$$d)zB$$$$$$$$$$$$$$%m(?z&$$#f'          
            ,n#$$BZ|]jZW$$$$$$$*r\h$$$$$$$$$$$$$Wmj](0%$$#u,            
              ,/d@$$MQt{(nQkW*c/q$$$$$$$$$BWh0n({tQM$$$dt,              
                 +vk@$$@o0u\)+I/nuzXXzvxt|))\nQa@$$@kv+.                 
                   .!/LhB$$$$BMhqZQLLQZqk*8$$$$BaL/!.                   
                       .;]fXOk#&B$$$$$$B&#kZYf]I.                       
                              '^:I!ii!l;"'                  
""";

    AnsiConsole.Write(
        new Panel(new Markup($"[grey]{Markup.Escape(logo)}[/]"))
            .Header("[bold]ASCII Logo[/]")
            .Border(BoxBorder.Rounded)
            .Expand());

    AnsiConsole.WriteLine();
    RenderCommandSection(commands);
}

static void RenderCommandSection(List<CommandSpec> commands)
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .Title("[bold]Command Section[/]")
        .AddColumn("[aqua]Input[/]")
        .AddColumn("[aqua]Behavior[/]");

    foreach (var command in commands)
    {
        table.AddRow(command.Name, command.Description);
    }

    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine("[grey]Type /command for direct actions, or plain text to see intent/intellisense matches.[/]");
}

static void ShowSuggestions(string query, List<CommandSpec> commands, string title)
{
    var normalized = query.Trim().ToLowerInvariant();
    var matches = commands
        .Where(c => c.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || c.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains(c.Name.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
        .Take(5)
        .ToList();

    if (matches.Count == 0)
    {
        AnsiConsole.MarkupLine($"[red]{title}:[/] no matches for [yellow]{Markup.Escape(query)}[/]. Try [aqua]/help[/].");
        return;
    }

    var list = new Rows(matches.Select(m =>
        new Markup($"[aqua]{Markup.Escape(m.Name)}[/] [grey]- {Markup.Escape(m.Description)}[/]")));

    AnsiConsole.Write(
        new Panel(list)
            .Header($"[bold]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded));
}

internal sealed record CommandSpec(string Name, string Description);
