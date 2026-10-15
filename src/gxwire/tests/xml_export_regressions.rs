use gxwire::cache::build_graph_from_directory;
use gxwire::model::{CallMode, EdgeKind, EntityKind};
use gxwire::parser::parse_genexus_source;
use std::fs;

#[test]
fn xml_method_link_targets_the_receiver_and_preserves_standalone_link() {
    let parsed = parse_genexus_source("<Source><![CDATA[\n&Icon.Link = CustomerDetails.Link(trnmode.Update, CustomerId)\nLink(CustomerList, CustomerId)\n]]></Source>");
    let calls: Vec<_> = parsed.references.iter().filter(|r| matches!(r.kind, EdgeKind::Calls(_)))
        .map(|r| (r.target_name.as_str(), r.kind.clone(), r.line)).collect();
    assert_eq!(calls, vec![
        ("CustomerDetails", EdgeKind::Calls(CallMode::Link), 2),
        ("CustomerList", EdgeKind::Calls(CallMode::Link), 3),
    ]);
}

#[test]
fn xml_export_folder_types_and_property_formulas_reach_the_graph() {
    let root = std::env::temp_dir().join(format!("gxwire_xml_regression_{}", std::process::id()));
    fs::create_dir_all(&root).unwrap();
    for (folder, name) in [("Procedure", "computeTotal"), ("Transaction", "Customer"), ("WebPanel", "Details"), ("Attribute", "Total") ] {
        fs::create_dir_all(root.join(folder)).unwrap();
        let body = if name == "Total" {
            "<Object type=\"00000000-0000-0000-0000-000000000000\">\n<Property><Name>Description</Name><Value>Ignored()</Value></Property>\n<Property><Name>Formula</Name><Value>computeTotal(CustomerId) IF CustomerId &gt; 0</Value></Property>\n</Object>"
        } else { "<Object type=\"00000000-0000-0000-0000-000000000000\"/>" };
        fs::write(root.join(folder).join(format!("{name}.xml")), body).unwrap();
    }
    let graph = build_graph_from_directory(&root).unwrap();
    for (name, kind) in [("computeTotal", EntityKind::Procedure), ("Customer", EntityKind::Transaction), ("Details", EntityKind::WebPanel), ("Total", EntityKind::Attribute)] {
        assert_eq!(graph.get_node(graph.find_node_id(name).unwrap()).unwrap().kind, kind);
    }
    let impact = gxwire::analysis::analyze_impact(&graph, "computeTotal").unwrap();
    assert_eq!(impact.formulas.len(), 1);
    assert_eq!(impact.formulas[0].name, "Total");
    assert!(!graph.get_callers(graph.find_node_id("computeTotal").unwrap()).iter()
        .any(|(_, kind, _)| matches!(kind, EdgeKind::Calls(_))));
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn xml_formula_reference_lines_and_nonformula_properties_are_distinct() {
    let source = "<Object>\n<Property><Name>Description</Name><Value>Ignored()</Value></Property>\n<Property>\n<Name>Formula</Name>\n<Value><![CDATA[\ncomputeTotal(0) +\ncomputeTax(0)\n]]></Value></Property>\n</Object>";
    let refs = gxwire::parser::lexer::formula_references(source);
    assert_eq!(refs.iter().map(|r| (r.target_name.as_str(), r.line)).collect::<Vec<_>>(),
        vec![("computeTotal", 6), ("computeTax", 7)]);
}

#[test]
fn sdk_type_guids_override_folders_and_filename_guesses() {
    let root = std::env::temp_dir().join(format!("gxwire_xml_guids_{}", std::process::id()));
    fs::create_dir_all(root.join("Transaction")).unwrap();
    for (name, guid) in [
        ("TrnCalculator", "84A12160-F59B-4AD7-A683-EA4481AC23E9"),
        ("ProcCustomer", "1db606f2-af09-4cf9-a3b5-b481519d28f6"),
        ("CustomerDetails", "c9584656-94b6-4ccd-890f-332d11fc2c25"),
    ] {
        fs::write(root.join("Transaction").join(format!("{name}.xml")),
            format!("<Object parentGuid='00000000-0000-0000-0000-000000000000' type='{{{guid}}}'/>" )).unwrap();
    }
    let graph = build_graph_from_directory(&root).unwrap();
    for (name, kind) in [("TrnCalculator", EntityKind::Procedure), ("ProcCustomer", EntityKind::Transaction), ("CustomerDetails", EntityKind::WebPanel)] {
        assert_eq!(graph.get_node(graph.find_node_id(name).unwrap()).unwrap().kind, kind);
    }
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn version_four_cached_classifications_are_reparsed() {
    use gxwire::cache::{build_index, CachedBundle, load_cache_from};
    let root = std::env::temp_dir().join(format!("gxwire_xml_cache_{}", std::process::id()));
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join("Calculator.xml"), "<Object type='84a12160-f59b-4ad7-a683-ea4481ac23e9'/>").unwrap();
    let (mut graph, mut parses) = build_index(&root).unwrap();
    graph.nodes[0].kind = EntityKind::Unknown;
    for parse in parses.values_mut() { parse.kind = EntityKind::Unknown; }
    let cache = root.join("legacy.bin");
    fs::write(&cache, bincode::serialize(&CachedBundle { version: 4, graph, parses }).unwrap()).unwrap();
    let loaded = load_cache_from(&cache, &root).unwrap();
    assert_eq!(loaded.get_node(loaded.find_node_id("Calculator").unwrap()).unwrap().kind, EntityKind::Procedure);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn native_workwith_folder_is_not_workwithplus() {
    let root = std::env::temp_dir().join(format!("gxwire_native_ww_{}", std::process::id()));
    fs::create_dir_all(root.join("WorkWithForWeb")).unwrap();
    fs::write(root.join("WorkWithForWeb").join("CustomerView.xml"),
        "<Object type='00000000-0000-0000-0000-000000000000'/>").unwrap();
    let graph = build_graph_from_directory(&root).unwrap();
    assert_eq!(graph.get_node(graph.find_node_id("CustomerView").unwrap()).unwrap().kind, EntityKind::Unknown);
    fs::remove_dir_all(root).unwrap();
}
