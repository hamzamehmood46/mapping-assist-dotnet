namespace Mapping.Core.Tests;

public class SourceReaderTests
{
    [Fact]
    public void Xml_leaves_become_paths_with_values()
    {
        var r = SourceReader.Read("<A><B>one</B><C><D> two </D></C></A>");
        Assert.True(r.Ok);
        Assert.Equal([("/A/B", "one"), ("/A/C/D", "two")], r.Items.Select(f => (f.Path, f.Value)));
    }

    [Fact]
    public void Attributes_are_fields_but_namespace_declarations_are_not()
    {
        var r = SourceReader.Read("""<A xmlns="urn:x" id="7"><B>v</B></A>""");
        Assert.Contains(r.Items, f => f is { Path: "/A/@id", Value: "7" });
        Assert.DoesNotContain(r.Items, f => f.Path.Contains("xmlns"));
    }

    [Fact]
    public void Only_the_first_of_a_repeated_element_is_read()
    {
        var r = SourceReader.Read(Samples.PartnerXml);
        Assert.Single(r.Items, f => f.Path == "/PurchaseOrder/Lines/Line/Sku");
        Assert.Equal("HX-220", r.Items.Single(f => f.Path.EndsWith("/Sku")).Value);
    }

    [Fact]
    public void Empty_elements_are_skipped()
    {
        var r = SourceReader.Read("<A><B/><C>x</C></A>");
        Assert.Equal(["/A/C"], r.Items.Select(f => f.Path));
    }

    [Theory]
    [InlineData("<A><B>x</A>")]
    [InlineData("<A></A>")]
    [InlineData("""<!DOCTYPE A [<!ENTITY x SYSTEM "file:///etc/passwd">]><A>&x;</A>""")]
    public void Bad_xml_is_an_error_not_an_exception(string xml)
    {
        var r = SourceReader.Read(xml);
        Assert.False(r.Ok);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void A_doctype_is_refused_so_a_sample_cannot_read_a_file()
    {
        var r = SourceReader.Read("""<!DOCTYPE A [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><A><B>&x;</B></A>""");
        Assert.False(r.Ok);
        Assert.Contains("DOCTYPE", r.Error);
    }

    [Fact]
    public void Edi_segments_become_positional_fields_and_known_ones_get_a_label()
    {
        var r = SourceReader.Read(Samples.EdiOrder);
        Assert.True(r.Ok);
        var po = r.Items.Single(f => f.Path == "BEG.03");
        Assert.Equal("PO-77310", po.Value);
        Assert.Equal("PurchaseOrderNumber", po.Label);
        Assert.Contains(r.Items, f => f is { Path: "PO1.06", Label: null });
    }

    [Fact]
    public void Empty_edi_elements_are_skipped_but_positions_are_kept()
    {
        var r = SourceReader.Read("BEG*00*SA*PO-1**20260101~");
        Assert.Equal(["BEG.01", "BEG.02", "BEG.03", "BEG.05"], r.Items.Select(f => f.Path));
    }

    [Fact]
    public void Only_the_first_of_a_repeated_segment_is_read()
    {
        var r = SourceReader.Read("PO1*1*8~PO1*2*3~");
        Assert.Equal(["PO1.01", "PO1.02"], r.Items.Select(f => f.Path));
        Assert.Equal("8", r.Items.Single(f => f.Path == "PO1.02").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData(null)]
    [InlineData("hello world this is not a sample")]
    [InlineData("BEG~")]
    public void Unusable_input_gives_an_error(string? text)
    {
        var r = SourceReader.Read(text);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void Too_many_fields_is_refused()
    {
        var xml = "<A>" + string.Concat(Enumerable.Range(0, 30).Select(i => $"<F{i}>v</F{i}>")) + "</A>";
        var r = SourceReader.Read(xml, maxFields: 10);
        Assert.False(r.Ok);
        Assert.Contains("limit is 10", r.Error);
    }

    [Fact]
    public void Newlines_work_as_segment_separators_too()
    {
        Assert.Equal(["N3.01", "N4.01"], SourceReader.Read("N3*1 Main St\nN4*Austin").Items.Select(f => f.Path));
    }
}

public class SchemaReaderTests
{
    [Fact]
    public void Lines_become_target_fields_with_defaults()
    {
        var r = SchemaReader.Read("/Order/Id\n/Order/Date | date | The date\n# comment\n\nN1.02 | number");
        Assert.True(r.Ok);
        Assert.Equal(new TargetField("/Order/Id", "string", ""), r.Items[0]);
        Assert.Equal(new TargetField("/Order/Date", "date", "The date"), r.Items[1]);
        Assert.Equal("number", r.Items[2].Type);
    }

    [Fact]
    public void A_description_may_contain_a_pipe()
    {
        Assert.Equal("a | b", SchemaReader.Read("/A/B | string | a | b").Items[0].Description);
    }

    [Theory]
    [InlineData("Order/Id")]
    [InlineData("/Order Id")]
    [InlineData("hello")]
    [InlineData("N1.2")]
    public void A_bad_path_is_refused_with_its_line_number(string path)
    {
        var r = SchemaReader.Read("/Ok/Path\n" + path);
        Assert.False(r.Ok);
        Assert.Contains("Line 2", r.Error);
    }

    [Fact]
    public void An_unknown_type_is_refused() => Assert.False(SchemaReader.Read("/A/B | boolean").Ok);

    [Fact]
    public void A_repeated_path_is_refused() => Assert.Contains("twice", SchemaReader.Read("/A/B\n/A/B").Error);

    [Theory]
    [InlineData("")]
    [InlineData("# only a comment")]
    [InlineData(null)]
    public void An_empty_schema_is_refused(string? text) => Assert.False(SchemaReader.Read(text).Ok);

    [Fact]
    public void Too_many_fields_is_refused()
    {
        var text = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"/A/F{i}"));
        Assert.Contains("limit is 10", SchemaReader.Read(text, 10).Error);
    }

    [Fact]
    public void An_attribute_path_is_allowed() => Assert.True(SchemaReader.Read("/Order/@id").Ok);

    [Fact]
    public void The_bundled_samples_parse()
    {
        Assert.True(SchemaReader.Read(Samples.InternalOrderSchema).Ok);
        Assert.True(SchemaReader.Read(Samples.EdiSchema).Ok);
        Assert.True(SourceReader.Read(Samples.PartnerXml).Ok);
        Assert.True(SourceReader.Read(Samples.EdiOrder).Ok);
    }
}

public class TransformTests
{
    [Theory]
    [InlineData("copy", " Ab ", " Ab ")]
    [InlineData("trim", "  Ab  ", "Ab")]
    [InlineData("upper", "ab", "AB")]
    [InlineData("lower", "AB", "ab")]
    [InlineData("isodate", "03/14/2026", "2026-03-14")]
    [InlineData("isodate", "20260321", "2026-03-21")]
    [InlineData("isodate", "2026-03-21", "2026-03-21")]
    [InlineData("isodate", "21-Mar-2026", "2026-03-21")]
    [InlineData("number", "$14.50", "14.50")]
    [InlineData("number", "1,234.5", "1234.5")]
    [InlineData("number", "-7", "-7")]
    [InlineData("number", "£ 9", "9")]
    public void Transforms_convert(string name, string input, string expected) => Assert.Equal(expected, Transforms.Apply(name, input));

    [Theory]
    [InlineData("isodate", "not a date")]
    [InlineData("isodate", "13/45/2026")]
    [InlineData("number", "twelve")]
    [InlineData("number", "1.2.3")]
    [InlineData("number", "")]
    [InlineData("rm -rf", "x")]
    public void A_value_that_cannot_be_converted_gives_null(string name, string input) => Assert.Null(Transforms.Apply(name, input));

    [Fact]
    public void Only_the_six_named_transforms_are_known()
    {
        Assert.Equal(["copy", "trim", "upper", "lower", "isodate", "number"], Transforms.Names);
        Assert.False(Transforms.IsKnown("eval"));
    }
}

public class OutputWriterTests
{
    private static KeyValuePair<string, string> P(string k, string v) => new(k, v);

    [Fact]
    public void Xml_paths_build_a_nested_document()
    {
        var r = OutputWriter.Render([P("/Order/Id", "1"), P("/Order/Customer/Name", "Acme"), P("/Order/Customer/Email", "a@b.example")]);
        Assert.Empty(r.Warnings);
        Assert.Contains("<Id>1</Id>", r.Text);
        Assert.Contains("<Name>Acme</Name>", r.Text);
        Assert.StartsWith("<Order>", r.Text);
    }

    [Fact]
    public void Special_characters_in_values_are_escaped()
    {
        var r = OutputWriter.Render([P("/A/B", "x < y & z")]);
        Assert.Contains("x &lt; y &amp; z", r.Text);
    }

    [Fact]
    public void An_attribute_path_sets_an_attribute()
    {
        var r = OutputWriter.Render([P("/Order/@id", "7"), P("/Order/Total", "3")]);
        Assert.Contains("id=\"7\"", r.Text);
    }

    [Fact]
    public void A_path_under_a_different_root_is_skipped_with_a_warning()
    {
        var r = OutputWriter.Render([P("/Order/Id", "1"), P("/Other/Id", "2")]);
        Assert.DoesNotContain("Other", r.Text);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void A_value_and_children_on_one_element_is_a_warning_not_a_crash()
    {
        var r = OutputWriter.Render([P("/A/B/C", "1"), P("/A/B", "2")]);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void An_invalid_xml_name_is_skipped()
    {
        var r = OutputWriter.Render([P("/Order/Id", "1"), P("/Order/1bad", "2")]);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Edi_elements_build_segments_with_empty_gaps()
    {
        var r = OutputWriter.Render([P("BEG.03", "PO-1"), P("BEG.05", "2026-01-01"), P("N1.02", "Acme")]);
        Assert.Equal("BEG***PO-1**2026-01-01~\nN1**Acme~", r.Text);
    }

    [Fact]
    public void Separators_inside_an_edi_value_are_replaced_and_reported()
    {
        var r = OutputWriter.Render([P("N1.02", "A*B~C")]);
        Assert.Equal("N1**A B C~", r.Text);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Mixed_styles_skip_the_odd_ones_out()
    {
        var r = OutputWriter.Render([P("N1.02", "Acme"), P("/Order/Id", "1")]);
        Assert.Single(r.Warnings);
        Assert.StartsWith("N1", r.Text);
    }

    [Fact]
    public void Nothing_in_gives_nothing_out() => Assert.Equal("", OutputWriter.Render([]).Text);
}
