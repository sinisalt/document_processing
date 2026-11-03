import type { FormSpec, FormSpecField } from "../lib/formSpec";
import deliveryNote from "../../../../../schemas/templates/std.delivery-note.v1.json";
import purchaseOrder from "../../../../../schemas/templates/std.purchase-order.v1.json";
import priceList from "../../../../../schemas/templates/std.price-list.v1.json";
import invoice from "../../../../../schemas/templates/std.invoice.v1.json";
import creditNote from "../../../../../schemas/templates/std.credit-note.v1.json";

/**
 * The compiled form specs of the shipped templates, from the snapshots the .NET compiler tests keep current
 * (`schemas/templates/*.v1.json`, `DefinitionCompilerTests`): the web tests render exactly what the API serves.
 */
export const templates = {
  deliveryNote: deliveryNote.formSpec as unknown as FormSpec,
  purchaseOrder: purchaseOrder.formSpec as unknown as FormSpec,
  priceList: priceList.formSpec as unknown as FormSpec,
  invoice: invoice.formSpec as unknown as FormSpec,
  creditNote: creditNote.formSpec as unknown as FormSpec,
};

/** A fork of a template with fields added at the top level (docs/11 §5.3), as the compiler would emit them. */
export function forkWith(spec: FormSpec, key: string, ...fields: FormSpecField[]): FormSpec {
  return { ...spec, key, version: spec.version + 1, fields: [...spec.fields, ...fields] };
}
