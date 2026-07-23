import { helper } from "./helper";

/** Adds two numbers together with a helper offset. */
export function add(a: number, b: number): number {
    return a + b + helper();
}
