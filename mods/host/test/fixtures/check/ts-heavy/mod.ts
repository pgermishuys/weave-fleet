import type { Register, Fleet } from "fleet-mods";
import { type Hook, type Next } from "fleet-mods";
export type { Hook };
interface Shape<T> { value: T; extra?: string }
type Pair<A, B = A> = [A, B];
declare const injected: number;
declare function ambient(x: number): void;
function overloaded(a: number): number;
function overloaded(a: string): string;
function overloaded(a: any): any { return a; }

abstract class Base<T> implements Shape<T> {
  private readonly secret: number = 1;
  protected static count?: number = 2;
  declare ghost: string;
  definite!: string;
  maybe?: number;
  abstract area(): number;
  public toString<U>(this: Base<T>, extra?: U): string { return String(this.secret) + String(extra); }
  [key: string]: unknown;
  value!: T;
  constructor(value: T) { this.value = value; }
}
class Child extends Base<number> implements Shape<number>, Iterable<number> {
  override area(): number { return 1; }
  *[Symbol.iterator](): Iterator<number> { yield 1; }
}

const asValue = (5 as unknown as number) satisfies number;
const assertion = <number>(asValue);
const bang = (assertion as number | undefined)!;
let late!: number;
late = bang;
const generic = async <T,>(input: T, flag?: boolean): Promise<T> => input;
const instance = new Map<string, number>();
const withArgs = Array.from<number>([1]);

export const register: Register = (on) => {
  on("ui.render", { component: "ToolUse" }, async ($: Fleet, e, next: Next<"ui.render">): Promise<any> => {
    const label: string = `${$.mod.name}`;
    const out = (await generic<string>(label)) as string;
    $.ui.log(out + new Child(1).area() + overloaded(1));
    return next(e);
  });
};
