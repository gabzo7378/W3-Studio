using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace W3Studio.Controls;

public static class VjassSyntaxHighlighter
{
    private static IHighlightingDefinition? _cachedDefinition;

    private const string VjassXshd = @"<?xml version=""1.0""?>
<SyntaxDefinition name=""vJASS"" extensions="".j;.vj;.zn"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
    <Color name=""Comment"" foreground=""#6A9955"" fontStyle=""italic"" />
    <Color name=""Preprocessor"" foreground=""#C586C0"" fontWeight=""bold"" />
    <Color name=""String"" foreground=""#CE9178"" />
    <Color name=""Rawcode"" foreground=""#FFD700"" fontWeight=""bold"" />
    <Color name=""Number"" foreground=""#B5CEA8"" />
    <Color name=""KeywordControl"" foreground=""#569CD6"" fontWeight=""bold"" />
    <Color name=""KeywordVjass"" foreground=""#C586C0"" fontWeight=""bold"" />
    <Color name=""TypeKeyword"" foreground=""#4EC9B0"" />
    <Color name=""ConstantKeyword"" foreground=""#4FC1FF"" />
    <Color name=""NativeCall"" foreground=""#DCDCAA"" />

    <RuleSet>
        <Span color=""Preprocessor"">
            <Begin>//!</Begin>
        </Span>

        <Span color=""Comment"">
            <Begin>//</Begin>
        </Span>

        <Span color=""Comment"" multiline=""true"">
            <Begin>/\*</Begin>
            <End>\*/</End>
        </Span>

        <Span color=""String"" multiline=""true"">
            <Begin>""</Begin>
            <End>""</End>
            <RuleSet>
                <Span begin=""\\"" end=""."" />
            </RuleSet>
        </Span>

        <Rule color=""Rawcode"">
            '[A-Za-z0-9_]{4}'
        </Rule>

        <Rule color=""Number"">
            \b0[xX][0-9a-fA-F]+\b|\b[0-9]+(\.[0-9]+)?\b
        </Rule>

        <Keywords color=""KeywordControl"">
            <Word>function</Word>
            <Word>endfunction</Word>
            <Word>takes</Word>
            <Word>returns</Word>
            <Word>nothing</Word>
            <Word>globals</Word>
            <Word>endglobals</Word>
            <Word>local</Word>
            <Word>set</Word>
            <Word>call</Word>
            <Word>if</Word>
            <Word>then</Word>
            <Word>elseif</Word>
            <Word>else</Word>
            <Word>endif</Word>
            <Word>loop</Word>
            <Word>exitwhen</Word>
            <Word>endloop</Word>
            <Word>return</Word>
            <Word>constant</Word>
            <Word>native</Word>
            <Word>array</Word>
            <Word>and</Word>
            <Word>or</Word>
            <Word>not</Word>
        </Keywords>

        <Keywords color=""KeywordVjass"">
            <Word>library</Word>
            <Word>endlibrary</Word>
            <Word>scope</Word>
            <Word>endscope</Word>
            <Word>struct</Word>
            <Word>endstruct</Word>
            <Word>method</Word>
            <Word>endmethod</Word>
            <Word>private</Word>
            <Word>public</Word>
            <Word>static</Word>
            <Word>readonly</Word>
            <Word>delegate</Word>
            <Word>interface</Word>
            <Word>endinterface</Word>
            <Word>module</Word>
            <Word>endmodule</Word>
            <Word>implement</Word>
            <Word>optional</Word>
            <Word>requires</Word>
            <Word>needs</Word>
            <Word>uses</Word>
            <Word>initializer</Word>
            <Word>operator</Word>
            <Word>hook</Word>
            <Word>thistype</Word>
            <Word>this</Word>
            <Word>allocate</Word>
            <Word>deallocate</Word>
            <Word>create</Word>
            <Word>destroy</Word>
            <Word>onDestroy</Word>
            <Word>onInit</Word>
            <Word>stub</Word>
            <Word>extends</Word>
            <Word>debug</Word>
            <Word>textmacro</Word>
            <Word>endtextmacro</Word>
            <Word>runtextmacro</Word>
        </Keywords>

        <Keywords color=""TypeKeyword"">
            <Word>integer</Word>
            <Word>real</Word>
            <Word>boolean</Word>
            <Word>string</Word>
            <Word>handle</Word>
            <Word>code</Word>
            <Word>unit</Word>
            <Word>item</Word>
            <Word>destructable</Word>
            <Word>widget</Word>
            <Word>player</Word>
            <Word>force</Word>
            <Word>group</Word>
            <Word>trigger</Word>
            <Word>triggercondition</Word>
            <Word>triggeraction</Word>
            <Word>timer</Word>
            <Word>location</Word>
            <Word>rect</Word>
            <Word>region</Word>
            <Word>effect</Word>
            <Word>lightning</Word>
            <Word>hashtable</Word>
            <Word>ability</Word>
            <Word>boolexpr</Word>
            <Word>conditionfunc</Word>
            <Word>filterfunc</Word>
            <Word>sound</Word>
            <Word>texttag</Word>
            <Word>timerdialog</Word>
            <Word>leaderboard</Word>
            <Word>multiboard</Word>
            <Word>quest</Word>
            <Word>dialog</Word>
            <Word>button</Word>
            <Word>trackable</Word>
            <Word>image</Word>
            <Word>ubersplat</Word>
            <Word>fogmodifier</Word>
        </Keywords>

        <Keywords color=""ConstantKeyword"">
            <Word>true</Word>
            <Word>false</Word>
            <Word>null</Word>
            <Word>bj_lastCreatedUnit</Word>
            <Word>bj_lastCreatedItem</Word>
            <Word>bj_lastCreatedEffect</Word>
            <Word>bj_lastCreatedTimer</Word>
            <Word>bj_lastCreatedGroup</Word>
            <Word>bj_DEGTORAD</Word>
            <Word>bj_RADTODEG</Word>
            <Word>bj_PI</Word>
            <Word>EVENT_PLAYER_UNIT_SPELL_EFFECT</Word>
            <Word>EVENT_PLAYER_UNIT_SPELL_CAST</Word>
            <Word>EVENT_PLAYER_UNIT_DEATH</Word>
            <Word>EVENT_PLAYER_UNIT_ATTACKED</Word>
            <Word>EVENT_PLAYER_UNIT_DAMAGED</Word>
            <Word>EVENT_UNIT_DEATH</Word>
            <Word>EVENT_UNIT_DAMAGED</Word>
            <Word>ATTACK_TYPE_NORMAL</Word>
            <Word>ATTACK_TYPE_HERO</Word>
            <Word>ATTACK_TYPE_CHAOS</Word>
            <Word>ATTACK_TYPE_MAGIC</Word>
            <Word>ATTACK_TYPE_PIERCE</Word>
            <Word>DAMAGE_TYPE_NORMAL</Word>
            <Word>DAMAGE_TYPE_MAGIC</Word>
            <Word>DAMAGE_TYPE_UNIVERSAL</Word>
        </Keywords>

        <Rule color=""NativeCall"">
            \b[A-Za-z_][A-Za-z0-9_]*(?=\s*\()
        </Rule>
    </RuleSet>
</SyntaxDefinition>";

    public static IHighlightingDefinition GetDefinition()
    {
        if (_cachedDefinition != null)
            return _cachedDefinition;

        using var sr = new StringReader(VjassXshd);
        using var reader = XmlReader.Create(sr);
        _cachedDefinition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        return _cachedDefinition;
    }
}

