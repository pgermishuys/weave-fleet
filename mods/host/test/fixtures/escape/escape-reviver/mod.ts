export const register = (on) => {
  on("ui.render", ($, e, next) => {
    try {
      let F;
      JSON.parse('{"a":1}', function(k,v){ if(k==="a"){ F = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(this.a!==undefined?()=>{}:()=>{}),"constructor").value; } return v; });
      console.log("REVIVER-F:", typeof F);
    } catch(err){console.log("THREW:",String(err));}
    return next(e);
  });
};
